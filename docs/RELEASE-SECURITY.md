# Release and update security

How a Filee release gets from a tag to a user's computer, what protects each step, and what is still open. The
workflows and scripts named here carry the details in their own comments.

## What a release goes through

1. **Build and test** (`.github/workflows/release.yml`, `portable.yml`): every action is pinned to a commit (a moved
   tag can't change what runs), the workflow token can only read except in the job that drafts the release, and every
   job has a time limit.
2. **Provenance and SBOM**: the packages, the tested Windows build and Setup get a build provenance attestation
   (`gh attestation verify <file> -R KnifeLemon/Filee` shows the workflow run that made them) and the release an SPDX
   SBOM (`Filee-<version>-sbom.spdx.json`).
3. **Signing**: Windows programs are signed with Filee's Authenticode certificate on a PC signed in to Certum's
   SimplySign (`build/sign-release.ps1`). The scripts find the certificate themselves and sign only if there is one:
   without it they warn and the build stays unsigned (`-RequireSignature` / no `-AllowUnsigned` make that an error).
4. **Checksums**: every package is listed in a `SHA256SUMS` file. With the update key (secret
   `FILEE_UPDATE_SIGNING_KEY`, or `-KeyFile` on the signing PC) the checksum files get a `.sig` (`build/sign-checksums.ps1`).

## What the updater checks (`UpdateService`)

| Check | When |
|---|---|
| SHA-256 of the installer against the release's checksum file | always |
| Signature of the checksum file with the update key | when the build has `FileeUpdatePublicKey` (`Directory.Build.props`); then a missing or wrong signature stops the update |
| Authenticode publisher of the installer = publisher of the running copy | signed copies |
| Authenticode publisher of the installer = `FileeExpectedPublisher` | copies that aren't signed themselves (built from source), when the build pins a publisher |

The checksum file sits on the same release as the installer: whoever can replace one can replace the other. The two
signatures can't be replaced with them. Linux and macOS have no Authenticode, so there the checksum signature is the
only check beyond the SHA-256.

### Turning the signatures on

1. Create the update key once: `pwsh build/sign-checksums.ps1 -NewKey -KeyFile <outside the repository>`. Keep the
   private key in a password manager and as the repository secret `FILEE_UPDATE_SIGNING_KEY` (the PEM text).
2. Put the printed public key into `<FileeUpdatePublicKey>` and the certificate subject (as
   `Get-AuthenticodeSignature` shows `SignerCertificate.Subject`) into `<FileeExpectedPublisher>`.
3. From that release on, sign every checksum file. Copies built before step 2 keep checking only the SHA-256 (and
   their Authenticode publisher), so nothing breaks for them.

Losing the private key means a new key and a release that carries the new public key, signed with the old one.

## Suggestions not done yet

- **Sign in CI.** Signing on a PC means nobody can prove that the published Windows files are the tested ones. A
  cloud signing service CI can call (for example Azure Trusted Signing, or Certum's SimplySign with a CI-capable
  token) would sign in the same run that tests, and the attestation would cover the signed files.
- **Reproducible builds.** `ContinuousIntegrationBuild`, `Deterministic` and a pinned SDK (`global.json` already) let
  anyone rebuild a release and compare it byte for byte.
- **Sign the Explorer menu package.** `FileeExplorerMenu.msix` is unsigned on purpose: its publisher carries the OID
  Windows requires for unsigned packages, and its family name derives from that publisher. Signing it needs a new
  publisher (the certificate's subject), a new family name, and Setup removing the old registration once; it might also
  avoid the registration error 0x80073CF6 of #450, but that is not confirmed.
- **macOS notarization** (#265), so Gatekeeper accepts the app without a warning.
- **Branch protection** on `main` and required reviews for `.github/workflows` and `build/` (CODEOWNERS), so a
  workflow change can't publish on its own.
