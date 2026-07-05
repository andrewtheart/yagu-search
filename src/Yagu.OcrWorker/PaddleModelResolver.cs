using System.Reflection;
using Sdcb.PaddleOCR.Models.Online;

namespace Yagu.OcrWorker;

/// <summary>
/// Maps a model name (e.g. <c>EnglishV4</c>, <c>ChineseV4</c>, <c>ChineseV5</c>) to the matching
/// <see cref="OnlineFullModels"/> instance. Falls back to <see cref="OnlineFullModels.ChineseV5"/>
/// when the name is unknown.
/// </summary>
internal static class PaddleModelResolver
{
    public const string DefaultModelName = "ChineseV5";

    public static OnlineFullModels Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return OnlineFullModels.ChineseV5;
        }

        FieldInfo? field = typeof(OnlineFullModels).GetField(
            name.Trim(),
            BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);

        if (field?.GetValue(null) is OnlineFullModels model)
        {
            return model;
        }

        return OnlineFullModels.ChineseV5;
    }
}
