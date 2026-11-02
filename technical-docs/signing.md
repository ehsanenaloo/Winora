# Code signing policy and plan

Status: PLAN. No Winora build is signed yet. Every artifact from `tools/publish.ps1` says UNSIGNED, and the release page must say so until this document says otherwise.

## What signing does and does not do

- Authenticode signing proves which publisher built a file and that it was not modified afterward. It is a prerequisite for the elevated worker in M4 (technical-docs/trust.md): an unsigned or wrongly signed worker must never be launched elevated.
- It does not remove Microsoft Defender SmartScreen warnings by itself. Reputation builds over time, per file and per publisher, for OV and EV certificates alike (see Microsoft's SmartScreen reputation documentation). Expect warnings on early releases; release notes explain how to verify hashes.

## Chosen route (the owner confirmed the repository will be public)

1. Publish the source in a public repository under GPL-3.0-only (the LICENSE is already in place).
2. Ship several unsigned 0.x releases with `SHA256SUMS`, so the project is already released in the form that will be signed.
3. Apply to the SignPath Foundation free open-source program. Check the current conditions at signpath.org before applying. At the time of writing they include: an OSI-approved license without commercial dual-licensing, no malware or unwanted software, active maintenance and a released product, functionality described on the download page, a fully automated build whose output is verifiable from the repository, and a code signing policy on the project home page.
4. If accepted, the publisher users see is **SignPath Foundation**, not Winora. Build the MSI with `-p:WinoraPublisher="SignPath Foundation"` (or the exact subject SignPath states) so the manufacturer text matches the certificate.
5. If refused, or if the owner wants their own publisher name: an OV certificate from a certificate authority (private key on hardware or a cloud HSM), or Azure Artifact Signing if the owner or organization is in an eligible country (individuals: United States and Canada; organizations: a listed set of countries; verify the current rules on Microsoft Learn). The MSI manufacturer must then equal that certificate's subject.

Never store a signing key or token in the repository, as a raw `.pfx` repository secret, or on a development machine.

## What the repository provides today

- The `WinoraPublisher` MSBuild property (installer/Winora.Installer/Winora.Installer.wixproj), default "Winora project", used as the MSI manufacturer.
- A reproducible unsigned portable zip and SBOM (ADR-009), so a signing service can verify that the binary came from the source.
- `.github/SECURITY.md`, this policy, and the marked place in `.github/workflows/windows.yml` where signing plugs in.

## Signing policy (publish on the project home page when signing starts)

- Only release builds produced by the project's CI from a tagged commit of the public repository are submitted for signing. No local or manual builds.
- Signed files: `Winora*.exe` and `Winora*.dll` in the staged layout, then the MSI. The portable zip is rebuilt from the signed files and its hash published.
- The publisher is the certificate subject, and release notes name it.
- Privacy: Winora has no telemetry, accounts or runtime network access. Diagnostics are local and redacted.
- Approval: the repository owner approves signing requests. Pull requests cannot trigger signing; it runs only on protected tags.

## CI changes needed once approved (not applied; no secrets exist)

1. A release workflow triggered only by protected tags, using the signing service's documented action or OIDC, with its token held in a protected environment that requires owner approval.
2. After packaging: submit the staged files, receive signed files, re-run the steps that depend on file contents (FILES.sha256, zip, SHA256SUMS), then sign the MSI.
3. `tools/verify_dist.ps1` must change from expecting NotSigned to verifying a valid signature from the expected publisher, with a timestamp.
4. Pin all GitHub Actions to full commit SHAs before the first public release (tags are used today because SHAs cannot be looked up from the offline development machine).

## Owner decisions still needed

Repository URL; whether to apply to SignPath Foundation now; who approves signing; the security contact (private vulnerability reporting is assumed); a legal name if a personal or organizational certificate is preferred.
