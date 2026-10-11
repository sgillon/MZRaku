# Setting the emulator up in launchers

Basic instructions for configuring popular emulation launchers to use MZRaku. This will be a work-in-progress.

## Launchbox

None of the Sharp machines MZRaku emulates are in Launchbox's standard platform list, so you'll be typing the platform names in yourself. The steps are the same for every machine — the only difference is the command-line switch that tells MZRaku which machine to start:

| Platform | Switch |
|---|---|
| Sharp MZ-700 | `--mz700` |
| Sharp MZ-80A | `--mz80a` |
| Sharp MZ-800 | `--mz800` |

The Associated Platforms screenshot shows all three machines set up, each with its own switch.

### Adding the emulator

- On the menu go to Tools -> Manage -> Emulators...

- Select the Add... button

- Add the details of MZRaku to the 'Details' pane of the 'Add Emulator' screen, with the path pointing to wherever you have installed it. The 'Sample Command' field should auto-populate ![Add emulator details settings](https://github.com/sgillon/MZRaku/blob/main/docs/launcher-images/lb-editemulator-details.png?raw=true)

- On the 'Associated Platforms' pane, type the platform name manually (e.g. "Sharp MZ-700") and optionally check the 'Default Emulator' box. Put the machine's switch from the table above in that row's 'Default Command-Line Parameters' column. 'Extract ROMs' can stay unticked — MZRaku opens `.zip` files itself. ![Set associated platform](https://github.com/sgillon/MZRaku/blob/main/docs/launcher-images/lb-editemulator-assocplatforms.png?raw=true)

- If you have games for more than one machine, add a row for each platform, each with its own switch. One MZRaku emulator entry covers them all.

- Press OK and then Close on the 'Manage Emulators' window

The switch matters even for the MZ-700, which is MZRaku's default machine: without it, MZRaku starts whichever machine is set as the default under System → Settings → Startup, so a game could open on the wrong machine.

### Adding games

- Games can now be added as normal, using the Import ROM files wizard *N.B. Any MZ games you add probably won't have an entry in the Launchbox Games Database (https://gamesdb.launchbox-app.com/). I'm hoping that, as a community, we can plug that gap*

- Assuming that you've set up MZRaku with each machine's ROM and BASIC files (see the [Quickstart](../../README.md#quickstart)), games can then be launched normally. The emulator will take care of working out whether to load BASIC or not before running the game.
