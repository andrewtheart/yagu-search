using System.Reflection;
using Sdcb.PaddleOCR.Models.Online;

namespace Yagu.OcrWorker;

/// <summary>
/// Maps a model name (e.g. <c>EnglishV4</c>, <c>ChineseV4</c>, <c>ChineseV5</c>) to the matching
/// <see cref="OnlineFullModels"/> instance. Falls back to <see cref="OnlineFullModels.EnglishV4"/>
/// when the name is unknown.
/// </summary>
internal static class PaddleModelResolver
{
    public const string DefaultModelName = "EnglishV4";

    public static OnlineFullModels Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return OnlineFullModels.EnglishV4;
        }

        FieldInfo? field = typeof(OnlineFullModels).GetField(
            name.Trim(),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);

        if (field?.GetValue(null) is OnlineFullModels model)
        {
            return model;
        }

        return OnlineFullModels.EnglishV4;
    }
}
