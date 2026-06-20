# Third-party notices

MZRaku is MIT-licensed (see [LICENSE](LICENSE)). The repository also
bundles a small number of third-party files under their own licenses,
listed here.

## ZEXDOC / ZEXALL — Frank Cringle, GPL v2

Files: `tools/CPM/zexdoc.com`, `tools/CPM/zexall.com`,
`tools/CPM/zexdoc.src`, `tools/CPM/zexall.src`, `tools/CPM/CPMRUN`.

These are Frank Cringle's Z80 instruction-set exercisers, distributed
under the GNU General Public License version 2. The license text is
preserved alongside the binaries at [`tools/CPM/Copying`](tools/CPM/Copying);
the Z80 assembly source is preserved as `zexdoc.src` / `zexall.src`.

MZRaku uses these as guest-software test inputs only — they are loaded
into the emulated Z80 at runtime via the Debug → Run Z80 Test… harness,
the same way the emulator runs any other CP/M `.com` file. No GPL code
is linked into the MZRaku build, and the release artefacts attached to
GitHub releases do not redistribute them. Their presence in the source
repository is a "mere aggregation" in the GPL v2 §2 sense and does not
affect the license of MZRaku itself.
