# Security policy

## Supported versions

Only the latest release of BroChoos Picker gets security fixes.

| Version | Supported |
| --- | --- |
| Latest 1.x release | Yes |
| Older releases | No |

## Report a vulnerability

Please do not open a public issue for a security problem.

Report it privately through GitHub: open the repository's **Security** tab and click **Report a vulnerability** ([direct link](https://github.com/HaYkMnE/BroChoos-Picker/security/advisories/new)).

Include:

- the BroChoos Picker version and your Windows version;
- what an attacker can do, and what they need first (for example, local access or a crafted link);
- steps to reproduce, or a proof of concept.

You will get a first reply within 7 days. We will agree on a disclosure date with you and credit you in the advisory unless you prefer otherwise.

Useful areas to look at: how links passed by Windows are parsed, the local named pipe between BroChoos windows, registry registration and unregistration, config file repair, and the planned, opt-in short-link helper (it cannot be turned on in this release).

## Verify your download

Download BroChoos Picker only from the [GitHub releases page](https://github.com/HaYkMnE/BroChoos-Picker/releases), winget (`BroChoos.BroChoosPicker`) or the Scoop bucket `HaYkMnE/scoop-bucket`.

1. Check the SHA-256 hash against `SHA256SUMS.txt` from the same release:

   ```powershell
   Get-FileHash .\BroChoos-Picker-Setup-<version>-x64.exe -Algorithm SHA256
   ```

2. Check the GitHub build provenance attestation with the [GitHub CLI](https://cli.github.com/):

   ```powershell
   gh attestation verify .\BroChoos-Picker-Setup-<version>-x64.exe -R HaYkMnE/BroChoos-Picker
   ```

If either check fails, do not run the file, and report it as described above.

The release workflow attests both the installer and the portable zip. The release files are not code-signed yet, so Windows SmartScreen may warn about an unknown publisher. The README explains [why and what to do](README.md#windows-protected-your-pc-heres-why).
