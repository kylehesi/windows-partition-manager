using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Localization;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>Small helpers over System.Management for the Storage Management namespace.</summary>
[SupportedOSPlatform("windows")]
internal static class Wmi
{
    public const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    public static ManagementScope Connect()
    {
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();
        return scope;
    }

    public static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var mo = (ManagementObject)item;
            yield return mo;
        }
    }

    /// <summary>First match or null; the caller owns the returned object.</summary>
    public static ManagementObject? QuerySingle(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            return (ManagementObject)item;
        }

        return null;
    }

    /// <summary>
    /// Invokes a Storage API method and throws a <see cref="StorageOperationException"/> with the
    /// ExtendedStatus text when ReturnValue is non-zero.
    /// </summary>
    public static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? setParameters = null)
    {
        ManagementBaseObject? inParams = null;
        try
        {
            if (setParameters is not null)
            {
                inParams = target.GetMethodParameters(method);
                setParameters(inParams);
            }

            var outParams = target.InvokeMethod(method, inParams, null)
                ?? throw new StorageOperationException(method, uint.MaxValue, "no result returned");

            var returnValue = Get<uint>(outParams, "ReturnValue");
            if (returnValue != 0)
            {
                var message = DescribeExtendedStatus(outParams) ?? DescribeReturnCode(returnValue);
                outParams.Dispose();
                throw new StorageOperationException(method, returnValue, message);
            }

            return outParams;
        }
        finally
        {
            inParams?.Dispose();
        }
    }

    /// <summary>Storage Management API return codes (learn.microsoft.com, "Storage Management API Common Return Codes").</summary>
    public static string DescribeReturnCode(uint code) => code switch
    {
        1 => CoreText.Get("StorageCode_1"),
        2 => CoreText.Get("StorageCode_2"),
        3 => CoreText.Get("StorageCode_3"),
        4 => CoreText.Get("StorageCode_4"),
        5 => CoreText.Get("StorageCode_5"),
        6 => CoreText.Get("StorageCode_6"),
        8 => CoreText.Get("StorageCode_8"),
        4097 => CoreText.Get("StorageCode_4097"),
        40000 => CoreText.Get("StorageCode_40000"),
        40001 => CoreText.Get("StorageCode_40001"),
        40002 => CoreText.Get("StorageCode_40002"),
        40003 => CoreText.Get("StorageCode_40003"),
        40004 => CoreText.Get("StorageCode_40004"),
        40005 => CoreText.Get("StorageCode_40005"),
        41000 => CoreText.Get("StorageCode_41000"),
        41001 => CoreText.Get("StorageCode_41001"),
        41002 => CoreText.Get("StorageCode_41002"),
        41003 => CoreText.Get("StorageCode_41003"),
        41004 => CoreText.Get("StorageCode_41004"),
        41005 => CoreText.Get("StorageCode_41005"),
        41006 => CoreText.Get("StorageCode_41006"),
        41010 => CoreText.Get("StorageCode_41010"),
        41011 => CoreText.Get("StorageCode_41011"),
        41012 or 41016 => CoreText.Get("StorageCode_41012"),
        41014 => CoreText.Get("StorageCode_41014"),
        41017 => CoreText.Get("StorageCode_41017"),
        42000 => CoreText.Get("StorageCode_42000"),
        42002 => CoreText.Get("StorageCode_42002"),
        42004 => CoreText.Get("StorageCode_42004"),
        42007 => CoreText.Get("StorageCode_42007"),
        42008 => CoreText.Get("StorageCode_42008"),
        42009 => CoreText.Get("StorageCode_42009"),
        42010 => CoreText.Get("StorageCode_42010"),
        42011 => CoreText.Get("StorageCode_42011"),
        42013 => CoreText.Get("StorageCode_42013"),
        43000 => CoreText.Get("StorageCode_43000"),
        43001 => CoreText.Get("StorageCode_43001"),
        43002 => CoreText.Get("StorageCode_43002"),
        43003 => CoreText.Get("StorageCode_43003"),
        43006 => CoreText.Get("StorageCode_43006"),
        _ => CoreText.Get("StorageCode_Other"),
    };

    private static readonly string[] StatusTextProperties = ["Message", "MessageDescription", "CIMStatusCodeDescription"];

    private static string? DescribeExtendedStatus(ManagementBaseObject outParams)
    {
        if (GetRaw(outParams, "ExtendedStatus") is not ManagementBaseObject status)
        {
            return null;
        }

        using (status)
        {
            var parts = StatusTextProperties
                .Select(p => Get<string>(status, p))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <summary>MSFT_* classes expose DriveLetter as a CIM char16; it is '\0' when no letter is assigned.</summary>
    public static char? ReadDriveLetter(ManagementBaseObject mo) => GetRaw(mo, "DriveLetter") switch
    {
        char c when c != '\0' && char.IsLetter(c) => char.ToUpperInvariant(c),
        string { Length: > 0 } s when char.IsLetter(s[0]) => char.ToUpperInvariant(s[0]),
        _ => null,
    };

    public static object? GetRaw(ManagementBaseObject mo, string property)
    {
        try
        {
            var value = mo[property];
            return value is DBNull ? null : value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public static T? Get<T>(ManagementBaseObject mo, string property)
    {
        var value = GetRaw(mo, property);
        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (InvalidCastException)
        {
            return default;
        }
        catch (FormatException)
        {
            return default;
        }
        catch (OverflowException)
        {
            return default;
        }
    }
}
