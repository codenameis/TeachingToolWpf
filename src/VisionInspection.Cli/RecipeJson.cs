using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionInspection.Cli;

public static class RecipeJson
{
    /// <summary>알 수 없는 필드와 숫자 enum을 거부해 잘못 입력한 조건으로 조용히 실행하지 않는다.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
