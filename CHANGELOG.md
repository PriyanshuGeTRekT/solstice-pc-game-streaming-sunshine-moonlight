# Changelog

## 1.0.1

**Fixed**
- Starting the **Xbox app** (and possibly other launchers) from the handheld ended the stream at once with Moonlight's "Something went wrong on your host PC" message. The Xbox app is opened through `explorer.exe`, which hands off to Windows and exits with code 1, and Sunshine took that as a failed launch. Launchers and games are now published as detached commands, which Sunshine starts without judging their exit code. Steam Big Picture keeps Sunshine's own entry.
- Entries published by 1.0.0 are updated automatically the next time Solstice opens. Nothing to re-add.
- **Recent sessions** and **Streaming now** show the app's name (for example "Xbox app") instead of "Desktop" for launchers.

## 1.0.0

First release.

- **Solstice (PC):** one-click setup that installs Sunshine and the ViGEmBus controller driver, starts the service, secures it with a private login, applies tuned settings, publishes your launchers and checks the hardware encoder
- Health page with one-click fixes: streaming engine, login, encoder, controller driver, network, firewall, wake-on-LAN, network type
- Live session view with **End session**, and recent sessions
- **Add a device:** serves Solstice Play to your handheld over the home network and approves pairing with matching four-digit codes, no PIN typing
- Library with launcher and game detection (Steam, Epic Games, EA app, Ubisoft Connect, Xbox app, GOG Galaxy, Battle.net, Playnite) and box art
- Tuning: Lowest latency, Balanced or Best picture encoder profiles; match the handheld's screen; hold View for the Guide button; keep Sunshine's admin page on this PC
- **Solstice Play (handheld):** one-tap setup that installs Moonlight, finds the PC without mDNS, pairs, sizes the stream to the screen and adds itself to the Start menu
- Play from tiles, wake the PC from sleep with Wake-on-LAN, stream settings sheet, full controller navigation
- Per-user installer with Start menu and desktop shortcuts and an entry in **Installed apps**
