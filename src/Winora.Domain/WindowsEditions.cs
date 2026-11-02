// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain;

/// <summary>Maps the <c>EditionID</c> of Windows to the four edition names a definition can name (Home, Pro, Enterprise, Education).</summary>
public static class WindowsEditions
{
    public static readonly IReadOnlyList<string> Names = ["Home", "Pro", "Enterprise", "Education"];

    /// <summary>The edition name of an <c>EditionID</c> value, or null for an unknown value (an unknown edition is never assumed to be supported).</summary>
    public static string? Classify(string? editionId) => editionId switch
    {
        "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
        "Professional" or "ProfessionalN" or "ProfessionalWorkstation" or "ProfessionalWorkstationN" or "ProfessionalEducation" or "ProfessionalEducationN" => "Pro",
        "Enterprise" or "EnterpriseN" or "EnterpriseS" or "EnterpriseSN" or "IoTEnterprise" or "IoTEnterpriseS" => "Enterprise",
        "Education" or "EducationN" => "Education",
        _ => null,
    };
}
