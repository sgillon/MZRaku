# Setting the emulator up in launchers

Basic instructions for configuring popular emulation launchers to use MZ700Emul. This will be a work-in-progress.

## Launchbox

### Adding the emulator

- On the menu go to Tools -> Manage -> Emulators...

- Select the Add... button

- Add the details of MZ700Emul to the 'Details' pane of the 'Add Emulator' screen, with the path pointing to wherever you have installed it. The 'Sample Command' field should auto-populate ![Add emulator details settings](https://github.com/sgillon/Mz700Emul/blob/main/docs/launcher-images/lb-editemulator-details.png?raw=true)

- On the 'Associated Platforms' pane, type "Sharp MZ-700" manually (it probably won't be listed in the default platforms dropdown) and optionally check the 'Default Emulator' box ![Set associated platform](https://github.com/sgillon/Mz700Emul/blob/main/docs/launcher-images/lb-editemulator-assocplatforms.png?raw=true)

- Press OK and then Close on the 'Manage Emulators' window

### Adding games

- Games can now be added as normal, using the Import ROM files wizard *N.B. Any MZ-700 games you add probably won't have an entry in the Launchbox Games Database (https://gamesdb.launchbox-app.com/). I'm hoping that, as a community, we can plug that gap*

- Assuming that you've set up MZ700Emul with the system ROM, font file and an S-BASIC .mzf, games can then be launched normally. The emulator will take care of working out whether to load BASIC or not before running the game.



