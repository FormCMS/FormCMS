using FormCMS.Utils.ResultExt;
using Microsoft.AspNetCore.WebUtilities;

namespace FormCMS.Cms.Handlers;

internal static class Util
{
    internal static T? ToEnum<T>(this string s)
    where T : struct, Enum
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        var ret = Enum.TryParse<T>(s, true, out var result);
        return ret? result : throw new ResultException($"'{s}' is not a valid value for {typeof(T).Name}");
    }
}