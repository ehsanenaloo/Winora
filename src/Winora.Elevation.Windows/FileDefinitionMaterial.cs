// SPDX-License-Identifier: GPL-3.0-only
using Winora.Elevation.Protocol.Definitions;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// Material read from a folder: <c>MachineDefinitions.v1.json</c>, <c>MachineDefinitions.v1.cose</c> and <c>pinned.spki</c> (one DER SubjectPublicKeyInfo). For the VmTrust build on a disposable
/// VM only; a release build embeds its material and never reads a folder.
/// </summary>
public sealed class FileDefinitionMaterial(string folder) : IDefinitionMaterial
{
    public IReadOnlyList<PinnedKey> Keys
    {
        get
        {
            var path = Path.Combine(folder, "pinned.spki");
            if (!File.Exists(path)) return [];
            try { return [new PinnedKey(File.ReadAllBytes(path))]; }
            catch (Exception e) when (e is ArgumentException or System.Security.Cryptography.CryptographicException or IOException) { return []; }
        }
    }

    public (byte[] Set, byte[] Signature)? Load()
    {
        var set = Path.Combine(folder, EmbeddedDefinitionMaterial.SetResource);
        var signature = Path.Combine(folder, EmbeddedDefinitionMaterial.SignatureResource);
        return File.Exists(set) && File.Exists(signature) ? (File.ReadAllBytes(set), File.ReadAllBytes(signature)) : null;
    }
}
