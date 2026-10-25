using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MZRaku.Hardware;

namespace MZRaku;

/// <summary>
/// A frame-stamped host-input script for <c>--keys=&lt;file&gt;</c>, used by
/// the regression suite to drive the emulator through the same key
/// handlers as a real keyboard. One command per line:
/// <code>
/// # frame  action
/// 300  type "PRINT 1+2"   ; each character pressed and released in turn
/// 372  key  Return        ; press, hold, release
/// 400  down ShiftKey      ; held until the matching up
/// 405  key  F1
/// 410  up   ShiftKey
/// </code>
/// A frame number means "after that many emulated frames since launch",
/// the same count <c>--dumpframe</c> uses until a reset: a reset
/// restarts the dump count but not the script's.
/// Frames must not go backwards, and a command can't start before a
/// previous <c>type</c> or <c>key</c> has released its last key.
///
/// Key names are <see cref="HostKey"/> names, the same as WinForms
/// <c>Keys</c> names. Like Windows, a left/right modifier
/// (<c>LShiftKey</c>) is reported as the generic one (<c>ShiftKey</c>),
/// and held modifiers are added to every later key's key data.
/// <c>key</c> and <c>down</c> send a text event only for Return, Back,
/// Tab, Escape and Space; printable characters must use <c>type</c>,
/// which maps each one to a key and Shift through a fixed UK layout and
/// sends the character itself, so a script gives the same result
/// whatever the host's keyboard layout.
/// </summary>
public sealed class KeyScript
{
    // Frames a typed or keyed press is held, and the gap after releasing
    // it. Matches the MZ-80A auto-typer's cadence; the hold outlasts the
    // two-frame shift staging in KeyboardMatrixBase.
    public const int HoldFrames = 4;
    public const int GapFrames = 4;

    public enum EventKind { Down, Up }

    /// <summary>
    /// One host event. A Down with <see cref="Text"/> is followed by a
    /// text (KeyPress) event unless a hotkey consumes the key.
    /// </summary>
    public readonly record struct Event(int Frame, EventKind Kind, HostKey KeyData, char? Text);

    private readonly List<Event> _events;
    private int _next;
    private int _frames;

    private KeyScript(List<Event> events) => _events = events;

    /// <summary>
    /// Count one emulated frame and hand every event now due to
    /// <paramref name="inject"/>, in script order.
    /// </summary>
    public void OnFrame(Action<Event> inject)
    {
        _frames++;
        while (_next < _events.Count && _events[_next].Frame <= _frames)
            inject(_events[_next++]);
    }

    public static KeyScript Load(string path) => Parse(File.ReadAllLines(path));

    /// <exception cref="FormatException">With the line number and the problem.</exception>
    public static KeyScript Parse(IEnumerable<string> lines)
    {
        var events = new List<Event>();
        var held = new HashSet<HostKey>();     // generic VKs held by down
        int lineNo = 0, lastFrame = 1, busyUntil = 0;

        foreach (var raw in lines)
        {
            lineNo++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            try
            {
                var (frameText, rest) = SplitWord(line);
                var (verb, arg) = SplitWord(rest);
                if (!int.TryParse(frameText, NumberStyles.None, CultureInfo.InvariantCulture, out int frame) || frame < 1)
                    throw new FormatException($"'{frameText}' isn't a frame number (1 or more)");
                if (frame < lastFrame)
                    throw new FormatException($"frame {frame} is earlier than the previous command's frame {lastFrame}");
                if (frame < busyUntil)
                    throw new FormatException($"frame {frame} is before the previous command finishes at frame {busyUntil} (its last release plus a {GapFrames}-frame gap)");
                lastFrame = frame;

                switch (verb)
                {
                    case "type":
                        busyUntil = AddType(events, frame, ParseQuoted(arg), held);
                        break;
                    case "key":
                    case "down":
                    {
                        var vk = ParseKey(arg);
                        var mods = Modifiers(held);
                        if (!IsModifier(vk) && (mods & (HostKey.Control | HostKey.Alt)) == 0 && IsPrintable(vk))
                            throw new FormatException($"{arg} types a character; use type \"...\" instead");
                        if (verb == "down" && !held.Add(vk))
                            throw new FormatException($"{arg} is already down");
                        // A modifier's own key-down already carries its flag, as on Windows.
                        var data = vk | mods | ModifierFlag(vk);
                        char? text = (mods & (HostKey.Control | HostKey.Alt)) == 0 ? ControlText(vk) : null;
                        events.Add(new Event(frame, EventKind.Down, data, text));
                        if (verb == "key")
                        {
                            events.Add(new Event(frame + HoldFrames, EventKind.Up, vk | mods, null));
                            busyUntil = frame + HoldFrames + GapFrames;
                        }
                        break;
                    }
                    case "up":
                    {
                        var vk = ParseKey(arg);
                        if (!held.Remove(vk))
                            throw new FormatException($"{arg} isn't down");
                        events.Add(new Event(frame, EventKind.Up, vk | Modifiers(held), null));
                        break;
                    }
                    default:
                        throw new FormatException($"unknown command '{verb}' (expected type, key, down or up)");
                }
            }
            catch (FormatException ex)
            {
                throw new FormatException($"line {lineNo}: {ex.Message}");
            }
        }
        // Up events from key/type are added out of frame order; the
        // dispatcher needs them sorted (stable, so same-frame order holds).
        StableSortByFrame(events);
        return new KeyScript(events);
    }

    private static int AddType(List<Event> events, int frame, string text, HashSet<HostKey> held)
    {
        if (held.Count > 0)
            throw new FormatException("type can't run while a key is held down");
        if (text.Length == 0)
            throw new FormatException("type needs at least one character");
        foreach (char c in text)
        {
            if (!UkLayout.TryGetValue(c, out var k))
                throw new FormatException($"'{c}' (U+{(int)c:X4}) has no key in the UK reference layout");
            var shift = k.Shift ? HostKey.Shift : HostKey.None;
            if (k.Shift) events.Add(new Event(frame, EventKind.Down, HostKey.ShiftKey | HostKey.Shift, null));
            events.Add(new Event(frame, EventKind.Down, k.Vk | shift, c));
            events.Add(new Event(frame + HoldFrames, EventKind.Up, k.Vk | shift, null));
            if (k.Shift) events.Add(new Event(frame + HoldFrames, EventKind.Up, HostKey.ShiftKey, null));
            frame += HoldFrames + GapFrames;
        }
        return frame;
    }

    private static void StableSortByFrame(List<Event> list)
    {
        // Insertion sort: lists are short and mostly ordered already.
        for (int i = 1; i < list.Count; i++)
        {
            var e = list[i];
            int j = i - 1;
            while (j >= 0 && list[j].Frame > e.Frame) { list[j + 1] = list[j]; j--; }
            list[j + 1] = e;
        }
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && quoted) { i++; continue; }
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '#' || c == ';')) return line.Substring(0, i);
        }
        return line;
    }

    private static (string word, string rest) SplitWord(string s)
    {
        s = s.TrimStart();
        int i = 0;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return (s.Substring(0, i), s.Substring(i).Trim());
    }

    private static string ParseQuoted(string arg)
    {
        if (arg.Length < 2 || arg[0] != '"' || arg[^1] != '"')
            throw new FormatException("type needs a quoted string, e.g. type \"RUN\"");
        var sb = new StringBuilder();
        for (int i = 1; i < arg.Length - 1; i++)
        {
            char c = arg[i];
            if (c == '\\')
            {
                if (++i >= arg.Length - 1) throw new FormatException("string ends with a lone \\");
                c = arg[i];
                if (c != '\\' && c != '"') throw new FormatException($"unknown escape \\{c} (only \\\\ and \\\" are allowed)");
            }
            else if (c == '"')
                throw new FormatException("unescaped \" inside the string");
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static HostKey ParseKey(string name)
    {
        if (name.Length == 0 || name.Contains(' ') || name.Contains(',') || char.IsDigit(name[0])
            || !Enum.TryParse<HostKey>(name, ignoreCase: false, out var k) || !Enum.IsDefined(k))
            throw new FormatException($"'{name}' isn't a Keys name (e.g. Return, F1, ShiftKey, Up)");
        if ((k & HostKey.Modifiers) != 0)
            throw new FormatException($"{name} is a modifier flag; use ShiftKey, ControlKey or Menu");
        // Windows reports the generic VK for left/right modifiers.
        return k switch
        {
            HostKey.LShiftKey or HostKey.RShiftKey => HostKey.ShiftKey,
            HostKey.LControlKey or HostKey.RControlKey => HostKey.ControlKey,
            HostKey.LMenu or HostKey.RMenu => HostKey.Menu,
            _ => k,
        };
    }

    private static bool IsModifier(HostKey vk) =>
        vk == HostKey.ShiftKey || vk == HostKey.ControlKey || vk == HostKey.Menu;

    private static HostKey ModifierFlag(HostKey vk) => vk switch
    {
        HostKey.ShiftKey => HostKey.Shift,
        HostKey.ControlKey => HostKey.Control,
        HostKey.Menu => HostKey.Alt,
        _ => HostKey.None,
    };

    private static HostKey Modifiers(HashSet<HostKey> held)
    {
        var m = HostKey.None;
        foreach (var vk in held) m |= ModifierFlag(vk);
        return m;
    }

    private static char? ControlText(HostKey vk) => vk switch
    {
        HostKey.Return => '\r',
        HostKey.Back => '\b',
        HostKey.Tab => '\t',
        HostKey.Escape => '\x1b',
        HostKey.Space => ' ',
        _ => null,
    };

    private static bool IsPrintable(HostKey vk) =>
        (vk >= HostKey.A && vk <= HostKey.Z) || (vk >= HostKey.D0 && vk <= HostKey.D9)
        || (vk >= HostKey.NumPad0 && vk <= HostKey.Divide)
        || (vk >= HostKey.OemSemicolon && vk <= HostKey.Oemtilde)
        || (vk >= HostKey.OemOpenBrackets && vk <= HostKey.Oem102);

    private readonly record struct UkKey(HostKey Vk, bool Shift);

    // The fixed reference layout `type` uses: a UK keyboard as Windows
    // reports it (kbduk). Only the key and Shift matter; the character
    // itself goes in the text event.
    private static readonly Dictionary<char, UkKey> UkLayout = BuildUkLayout();

    private static Dictionary<char, UkKey> BuildUkLayout()
    {
        var m = new Dictionary<char, UkKey>();
        for (char c = 'a'; c <= 'z'; c++)
        {
            m[c] = new UkKey(HostKey.A + (c - 'a'), false);
            m[char.ToUpperInvariant(c)] = new UkKey(HostKey.A + (c - 'a'), true);
        }
        for (char c = '0'; c <= '9'; c++) m[c] = new UkKey(HostKey.D0 + (c - '0'), false);
        const string shiftedDigits = ")!\"£$%^&*(";   // Shift+0 .. Shift+9
        for (int i = 0; i < 10; i++) m[shiftedDigits[i]] = new UkKey(HostKey.D0 + i, true);
        void Pair(HostKey vk, char plain, char shifted)
        {
            m[plain] = new UkKey(vk, false);
            m[shifted] = new UkKey(vk, true);
        }
        Pair(HostKey.OemMinus, '-', '_');
        Pair(HostKey.Oemplus, '=', '+');
        Pair(HostKey.OemOpenBrackets, '[', '{');
        Pair(HostKey.OemCloseBrackets, ']', '}');
        Pair(HostKey.OemSemicolon, ';', ':');
        Pair(HostKey.Oemtilde, '\'', '@');       // VK_OEM_3 on UK
        Pair(HostKey.OemQuotes, '#', '~');       // VK_OEM_7 on UK
        Pair(HostKey.Oemcomma, ',', '<');
        Pair(HostKey.OemPeriod, '.', '>');
        Pair(HostKey.OemQuestion, '/', '?');
        Pair(HostKey.OemPipe, '\\', '|');        // VK_OEM_5, beside Z on UK
        Pair(HostKey.Oem8, '`', '¬');
        m[' '] = new UkKey(HostKey.Space, false);
        return m;
    }
}
