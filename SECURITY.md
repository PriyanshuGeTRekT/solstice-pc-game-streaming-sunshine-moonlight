# Security policy

## Supported versions

Only the [latest release](https://github.com/PriyanshuGeTRekT/solstice-pc-game-streaming-sunshine-moonlight/releases/latest) of Solstice receives security fixes. Please update to it and check that the problem still happens before reporting.

| Version        | Supported |
| -------------- | --------- |
| Latest release | Yes       |
| Older releases | No        |

## Reporting a vulnerability

**Please do not report security problems in public issues, discussions or pull requests.**

Report them privately through GitHub:

1. Open the [Security tab](https://github.com/PriyanshuGeTRekT/solstice-pc-game-streaming-sunshine-moonlight/security) of this repository.
2. Click **Report a vulnerability**.
3. Describe the problem, the Solstice and Windows versions affected, and the steps to reproduce it. A proof of concept helps.

You will get a reply as soon as possible, usually within a week. Once the problem is confirmed, a fix is prepared and released, and the advisory is published with credit to you unless you prefer to stay anonymous.

Problems in Sunshine or Moonlight themselves should go to [Sunshine's security policy](https://github.com/LizardByte/Sunshine/security) or [Moonlight's](https://github.com/moonlight-stream/moonlight-qt/security).

## Scope

Examples of what counts as a security problem in Solstice:

- A device being paired with the PC without someone approving it in Solstice (or typing its PIN).
- Another program or web page on the PC driving Solstice's local interface (it is bound to 127.0.0.1 and requires a per-launch session cookie).
- The device-setup listener (TCP 47800) being reachable from outside the local subnet, staying open longer than its 15-minute window, or serving anything other than the setup page, the Solstice Play download and the pairing request.
- The saved Sunshine login being readable by another Windows account (it is encrypted with DPAPI for the current user).
- Solstice running anything with administrator rights that a standard user could have modified.

Solstice and Solstice Play are not code-signed, so Windows SmartScreen warns on first launch. That alone is not a vulnerability.
