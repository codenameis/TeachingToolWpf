using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Versioned JSON persistence serializer for <see cref="IndustrialOcrFontModel"/>.
/// Never assumes implicit file paths; callers provide an explicit <see cref="Stream"/>.
/// </summary>
public static class IndustrialOcrFontModelSerializer
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Save(Stream destination, IndustrialOcrFontModel model)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(model);

        var templates = model.Templates;
        var dtoTemplates = new GlyphTemplateDto[templates.Length];
        for (int i = 0; i < templates.Length; i++)
        {
            IndustrialOcrGlyphTemplate t = templates[i];
            dtoTemplates[i] = new GlyphTemplateDto(
                t.Character.ToString(),
                t.NormalizedGrid.ToArray(),
                t.Cl2nFeature.ToArray(),
                t.HorizontalProjection.ToArray(),
                t.VerticalProjection.ToArray(),
                t.QuadrantDensities.ToArray());
        }

        var envelope = new FontModelEnvelopeDto(
            CurrentFormatVersion,
            IndustrialOcrGlyphTemplate.GridWidth,
            IndustrialOcrGlyphTemplate.GridHeight,
            model.GlobalMean.ToArray(),
            dtoTemplates);

        JsonSerializer.Serialize(destination, envelope, JsonOptions);
    }

    public static IndustrialOcrFontModel Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        FontModelEnvelopeDto? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FontModelEnvelopeDto>(source, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("티칭 폰트 파일 JSON 형식이 손상되었습니다.", ex);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("티칭 폰트 파일이 비어 있습니다.");
        }

        if (envelope.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"지원하지 않는 티칭 폰트 형식 버전입니다 (파일 버전: {envelope.FormatVersion}, 지원 버전: {CurrentFormatVersion}).");
        }

        if (envelope.GridWidth != IndustrialOcrGlyphTemplate.GridWidth ||
            envelope.GridHeight != IndustrialOcrGlyphTemplate.GridHeight)
        {
            throw new InvalidDataException(
                $"그리드 규격 불일치: {envelope.GridWidth}×{envelope.GridHeight} (기대: {IndustrialOcrGlyphTemplate.GridWidth}×{IndustrialOcrGlyphTemplate.GridHeight}).");
        }

        if (envelope.GlobalMean is null || envelope.GlobalMean.Length != IndustrialOcrGlyphTemplate.GridLength)
        {
            throw new InvalidDataException("티칭 폰트 GlobalMean 벡터 길이가 유효하지 않습니다.");
        }

        if (envelope.Templates is null || envelope.Templates.Length == 0)
        {
            throw new InvalidDataException("티칭 폰트에 등록된 문자 템플릿이 없습니다.");
        }

        var templates = new IndustrialOcrGlyphTemplate[envelope.Templates.Length];
        for (int i = 0; i < envelope.Templates.Length; i++)
        {
            GlyphTemplateDto dto = envelope.Templates[i]
                ?? throw new InvalidDataException($"문자 템플릿 항목 #{i}이 null입니다.");

            if (string.IsNullOrEmpty(dto.Character) || dto.Character.Length != 1)
            {
                throw new InvalidDataException($"문자 템플릿 항목 #{i}의 문자 기호가 유효하지 않습니다.");
            }

            if (dto.NormalizedGrid is null || dto.NormalizedGrid.Length != IndustrialOcrGlyphTemplate.GridLength ||
                dto.Cl2nFeature is null || dto.Cl2nFeature.Length != IndustrialOcrGlyphTemplate.GridLength ||
                dto.HorizontalProjection is null || dto.HorizontalProjection.Length != IndustrialOcrGlyphTemplate.GridHeight ||
                dto.VerticalProjection is null || dto.VerticalProjection.Length != IndustrialOcrGlyphTemplate.GridWidth ||
                dto.QuadrantDensities is null || dto.QuadrantDensities.Length != IndustrialOcrGlyphTemplate.QuadrantCount)
            {
                throw new InvalidDataException($"문자 '{dto.Character}' 템플릿 배열 길이가 손상되었습니다.");
            }

            templates[i] = new IndustrialOcrGlyphTemplate(
                dto.Character[0],
                (float[])dto.NormalizedGrid.Clone(),
                (float[])dto.Cl2nFeature.Clone(),
                (float[])dto.HorizontalProjection.Clone(),
                (float[])dto.VerticalProjection.Clone(),
                (float[])dto.QuadrantDensities.Clone());
        }

        return new IndustrialOcrFontModel(templates, (float[])envelope.GlobalMean.Clone());
    }

    private sealed record FontModelEnvelopeDto(
        int FormatVersion,
        int GridWidth,
        int GridHeight,
        float[] GlobalMean,
        GlyphTemplateDto[] Templates);

    private sealed record GlyphTemplateDto(
        string Character,
        float[] NormalizedGrid,
        float[] Cl2nFeature,
        float[] HorizontalProjection,
        float[] VerticalProjection,
        float[] QuadrantDensities);
}
