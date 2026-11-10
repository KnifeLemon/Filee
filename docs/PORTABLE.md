### True portable mode

Portable release archives include an empty `filee.portable` beside `Filee.exe` (or the `Filee` executable on Linux).
For other copies, create this marker before starting Filee to enable portable mode.
On macOS, put the marker beside `Filee.app`, outside the bundle. Settings, downloaded engines and logs then live
in a `data` folder beside the marker. Both the app and CLI use it. Login startup and file-manager integration
are disabled, leaving an installed copy's entries untouched. The data folder must be writable; Filee reports
an error instead of falling back to the user profile.

Conversion work folders, temporary files and engine caches keep their existing behavior and locations.
An explicit `FILEE_DATA_DIR` still takes precedence.
Installer packages keep using the normal user profile.
Update a marked copy by replacing its application files while keeping `filee.portable` and `data`.

### Bringing existing data along

Close the previous Filee copy before starting the portable app. If portable data has no settings, library,
history or downloaded engines and the normal profile contains `settings.json`, the app offers a one-time import.
It copies settings, presets, profiles, history and downloaded engines, with percentage progress. Originals stay
untouched. Settings, presets, profiles and history each have their own checkbox; downloaded engines are one
additional group, including shared dependencies. Available choices are selected by default. The engines choice
shows their size when it can be measured. Custom profiles may need their matching presets; import both to keep
custom actions. History can be excluded independently. Unselected engines can be installed later; without imported
settings, normal setup opens.
Choose **Import existing data** to copy the selected categories, or **Start fresh** to use defaults and
remember that choice. This appears before Home and engine setup. A failed copy can be retried;
existing portable data is never overwritten. Start the desktop app before using the CLI if you want to import.
Custom engine paths remain external; downloaded copies travel with the portable folder.

If this same executable previously registered startup or file-manager integration on Windows, the app asks before
removing those entries. Entries targeting another executable are preserved. Cleanup is offered independently of
import, so declining import does not hide obsolete integration. If cleanup fails or is cancelled, it is offered
again next time while owned entries remain. `--uninstall-cleanup` also removes only this copy's entries.

### macOS quarantine

Extract the tar.gz into a writable folder and keep the marker beside `Filee.app`. Finder can launch a quarantined
app from an App Translocation path, where the marker is not visible. After verifying the download's source and
checksum, remove quarantine from the extracted app before launching:

```sh
xattr -dr com.apple.quarantine /path/to/Filee.app
```

The .dmg is an installation image, not a writable portable folder. Installation copies only the app bundle.
