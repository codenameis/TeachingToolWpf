using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionInspection.Cli;

public static class RecipeJson
{
    /// <summary>알 수 없는 필드와 숫자 enum을 거부해 잘못 입력한 조건으로 조용히 실행하지 않는다.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(SearchOptions))
            {
                return;
            }
            var limit = info.Properties.Single(property => property.Name == "maximumFitErrorPixels");
            // JSON에는 Infinity 숫자가 없다. 생략 시 계약의 무제한 기본값으로 복원한다.
            limit.ShouldSerialize = (_, value) => value is not double number || !double.IsPositiveInfinity(number);
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = resolver,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
