# AI Desktop Setup

AI Desktop Setup is a Windows assistant for installing and configuring desktop AI
clients with a setup code from your chosen gateway. The current adapter supports
the **Codex desktop client**.

The assistant guides you through confirming the service, installing the official
client, and applying the gateway configuration to your Windows account. It backs
up existing configuration and lets you resume an interrupted setup.

Setup codes use the [AI Gateway Setup Protocol (AGSP)](protocol/README.md). Your
gateway must implement AGSP and supply a supported Codex configuration; an API
endpoint and key alone are not a setup code.

## Get started

1. Get a setup code from an AGSP-compatible gateway you trust.
2. Open [Releases](https://github.com/macworld/ai-desktop-setup/releases), read the
   release notes, and download `AI-Desktop-Setup-<version>-x64.exe` or
   `AI-Desktop-Setup-<version>-arm64.exe` for your native Windows architecture.
3. Run the assistant and choose **Paste and preview**, or enter the code and choose
   **Preview typed code**. Check the displayed setup service and API addresses.
   Opening the assistant and previewing a code make no network requests.
4. Choose **Confirm addresses and authenticate**, then **Install and configure**.
   Windows may ask for administrator permission to install the official client.
   Configuration is written for the Windows account that started setup.
5. When setup completes, choose **Open desktop client**. If Windows requests a
   restart or sign-out, follow those instructions and open Codex from the Start menu.

The assistant downloads the official desktop package when installation is needed;
it does not bundle Codex. Read the [privacy guide](privacy.md) before connecting
to a gateway.

## Requirements

| Native architecture | Windows version | Runtime used by the assistant |
| --- | --- | --- |
| x64 | Windows 10 version 2004 or later | .NET Framework 4.8 |
| ARM64 | Windows 11 version 22H2 or later | .NET Framework 4.8.1 |

Choose the package for the operating system's native architecture, including on
ARM64 systems that can run emulated x64 applications. The assistant uses the
Framework runtime provided by Windows; you do not need the .NET 10 runtime.
Network access is required for authentication, downloads, and package trust checks.
The official client's own Windows requirements are also checked before installation.

## Recovery

For a failed setup, select an entry under **Interrupted setup on this account**
and choose **Resume installation / retry receipt**. After a crash or unexpected
interruption, reopen the assistant on the same Windows account to use retained
recovery. If configuration was already committed, recovery retries the completion
receipt without writing credentials again.

If a selected download cannot be used, **Retry with a fresh official download**
downloads and verifies a new package from the official source. **Cancel and clear
recovery** or closing the assistant normally clears pending recovery. Installed
clients, configuration, and gateway credentials remain. See
[removal and recovery](uninstall.md) for backup restoration.

## Build and contribute

Local builds require .NET SDK **10.0.401**, NSIS **3.12**, Python 3, and Bash:

```sh
./scripts/build-windows.sh
```

Set `AI_SETUP_DOTNET` or `AI_SETUP_MAKENSIS` to select installed tools. The script
runs the shared .NET 10 tests, builds the .NET Framework tests, and packages both
architectures into `artifacts/`.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and testing, and
[Actions](https://github.com/macworld/ai-desktop-setup/actions) for CI results.
Implementation and release details are in the [Codex adapter guide](docs/codex-adapter.md),
[Windows acceptance matrix](docs/windows-acceptance.md),
[code signing policy](docs/code-signing-policy.md), and
[signed release workflow](docs/signed-release.md). Report vulnerabilities through
the [security policy](SECURITY.md).

Project source is licensed under [MIT](LICENSE). Third-party components and build
tools retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
