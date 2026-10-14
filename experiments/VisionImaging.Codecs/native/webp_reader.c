#include "reader.h"
#include <webp/decode.h>
#include <stdlib.h>
#include <string.h>

typedef struct
{
    vi_reader base;
    uint8_t *pixels;
} webp_reader;

static void destroy_webp(vi_reader *base)
{
    free(((webp_reader *)base)->pixels);
}

static int read_webp(vi_reader *base, uint32_t rows, uint8_t *pixels, const int32_t *cancel)
{
    if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "WebP read cancelled.");
    size_t stride = (size_t)base->info.width * 4;
    memcpy(pixels, ((webp_reader *)base)->pixels + (size_t)base->next_row * stride, stride * rows);
    base->next_row += rows;
    return VI_OK;
}

int vi_webp_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **result, vi_error *error)
{
    webp_reader *reader = calloc(1, sizeof(*reader));
    if (!reader) { fclose(file); return error->code = VI_BUDGET; }
    vi_reader *base = &reader->base;
    base->file = file;
    base->budget = budget;
    base->destroy = destroy_webp;
    base->read = read_webp;
    *result = base;
    uint8_t header[12];
    uint8_t *input = NULL;
    int status = VI_OK;
    if (fread(header, 1, sizeof(header), file) != sizeof(header))
    { status = vi_fail(base, VI_INVALID, "Truncated WebP header."); goto done; }
    uint64_t length = 8 + ((uint64_t)header[4] | (uint64_t)header[5] << 8 |
                          (uint64_t)header[6] << 16 | (uint64_t)header[7] << 24);
    if (length < sizeof(header))
    { status = vi_fail(base, VI_INVALID, "Invalid WebP RIFF length."); goto done; }
    if (budget < sizeof(*reader) || length > budget - sizeof(*reader))
    { status = vi_fail(base, VI_BUDGET, "WebP compressed input exceeds budget."); goto done; }
    input = malloc((size_t)length);
    if (!input)
    { status = vi_fail(base, VI_BUDGET, "Cannot allocate WebP input."); goto done; }
    memcpy(input, header, sizeof(header));
    for (size_t offset = sizeof(header); offset < (size_t)length;)
    {
        if (vi_cancelled(cancel))
        { status = vi_fail(base, VI_CANCELLED, "WebP input cancelled."); goto done; }
        size_t count = (size_t)length - offset;
        if (count > 65536) count = 65536;
        if (fread(input + offset, 1, count, file) != count)
        { status = vi_fail(base, ferror(file) ? VI_IO : VI_INVALID, "Truncated WebP input."); goto done; }
        offset += count;
    }
    if (fgetc(file) != EOF || ferror(file))
    { status = vi_fail(base, VI_INVALID, "WebP length does not match its RIFF container."); goto done; }
    WebPDecoderConfig config;
    if (!WebPInitDecoderConfig(&config))
    { status = vi_fail(base, VI_UNSUPPORTED, "WebP decoder ABI mismatch."); goto done; }
    VP8StatusCode decoded = WebPGetFeatures(input, (size_t)length, &config.input);
    if (decoded != VP8_STATUS_OK)
    { status = vi_fail(base, VI_INVALID, "Invalid WebP bitstream."); goto done; }
    if (config.input.has_animation)
    { status = vi_fail(base, VI_UNSUPPORTED, "Animated WebP is not supported."); goto done; }
    if (config.input.width <= 0 || config.input.height <= 0 || config.input.width > 16383 || config.input.height > 16383)
    { status = vi_fail(base, VI_INVALID, "Invalid WebP dimensions."); goto done; }
    uint64_t output = (uint64_t)config.input.width * (uint64_t)config.input.height * 4;
    if (output > budget - sizeof(*reader) - length)
    { status = vi_fail(base, VI_BUDGET, "WebP RGBA output and input exceed budget."); goto done; }
    reader->pixels = malloc((size_t)output);
    if (!reader->pixels)
    { status = vi_fail(base, VI_BUDGET, "Cannot allocate WebP RGBA output."); goto done; }
    config.output.colorspace = MODE_RGBA;
    config.output.is_external_memory = 1;
    config.output.u.RGBA.rgba = reader->pixels;
    config.output.u.RGBA.stride = config.input.width * 4;
    config.output.u.RGBA.size = (size_t)output;
    if (vi_cancelled(cancel))
    { status = vi_fail(base, VI_CANCELLED, "WebP decode cancelled."); goto done; }
    // Full-frame decoding: budget covers input/output, not libwebp's internal working memory.
    decoded = WebPDecode(input, (size_t)length, &config);
    WebPFreeDecBuffer(&config.output);
    if (decoded != VP8_STATUS_OK)
    { status = vi_fail(base, decoded == VP8_STATUS_OUT_OF_MEMORY ? VI_BUDGET : VI_INVALID, "WebP decode failed."); goto done; }
    if (vi_cancelled(cancel))
    { status = vi_fail(base, VI_CANCELLED, "WebP decode cancelled."); goto done; }
    base->info.width = config.input.width;
    base->info.height = config.input.height;
    base->info.kind = VI_RGBA32;
done:
    free(input);
    *error = base->error;
    return status;
}
