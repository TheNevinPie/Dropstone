# Privacy policy

Dropstone is a local Windows installer and sync worker. It does not include telemetry, analytics, crash reporting, advertising, or automatic update checks.

## Local data

Dropstone reads local LeviLauncher configuration, registry entries, running processes, and version folders to locate the game installation. It writes its install state, logs, worker files, scheduled task, WMI event subscription, and the payload files selected by the user. These remain on the user's computer. Uninstall removes the items listed in the README.

## Network access

Dropstone does not transfer information to other networked systems unless specifically requested by the user or the person installing or operating it.

If the user configures a payload URL and confirms the install, Dropstone downloads that URL. The request necessarily discloses standard connection data, such as the user's IP address and HTTP headers, to the host serving the payload. That host's privacy policy applies. Dropstone does not send the payload or install details anywhere else.

## Contact

Open an issue at https://github.com/TheNevinPie/Dropstone/issues.
