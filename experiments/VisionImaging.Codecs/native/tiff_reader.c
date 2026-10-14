#include "reader.h"
#include <tiffio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#ifdef _WIN32
#include <io.h>
#define vi_seek _fseeki64
#define vi_tell _ftelli64
#else
#include <unistd.h>
#define vi_seek fseeko
#define vi_tell ftello
#endif

typedef struct
{
    vi_reader base;
    TIFF *tiff;
    uint16_t samples, bits, photo;
    uint32_t tile_width, tile_height, band_top;
    size_t input_stride, tile_stride;
    uint8_t *band, *tile;
} tiff_reader;

static tmsize_t read_file(thandle_t handle, void *data, tmsize_t size)
{
    return size < 0 ? -1 : (tmsize_t)fread(data, 1, (size_t)size, ((vi_reader *)handle)->file);
}
static tmsize_t write_file(thandle_t handle, void *data, tmsize_t size)
{ (void)handle; (void)data; (void)size; return -1; }
static toff_t seek_file(thandle_t handle, toff_t offset, int origin)
{
    FILE *file = ((vi_reader *)handle)->file;
    if (vi_seek(file, (int64_t)offset, origin) != 0) return (toff_t)-1;
    return (toff_t)vi_tell(file);
}
static int close_file(thandle_t handle)
{
    vi_reader *reader = handle;
    int status = fclose(reader->file);
    reader->file = NULL;
    return status;
}
static toff_t size_file(thandle_t handle)
{
    FILE *file = ((vi_reader *)handle)->file;
    int64_t position = vi_tell(file);
    if (position < 0 || vi_seek(file, 0, SEEK_END)) return 0;
    int64_t size = vi_tell(file);
    if (vi_seek(file, position, SEEK_SET)) return 0;
    return size < 0 ? 0 : (toff_t)size;
}
static int map_file(thandle_t handle, void **base, toff_t *size)
{ (void)handle; (void)base; (void)size; return 0; }
static void unmap_file(thandle_t handle, void *base, toff_t size)
{ (void)handle; (void)base; (void)size; }
static int tiff_error(TIFF *tiff, void *data, const char *module, const char *format, va_list args)
{
    (void)tiff; (void)module;
    vi_reader *reader = data;
    reader->error.code = VI_INVALID;
    vsnprintf(reader->error.message, sizeof(reader->error.message), format, args);
    return 1;
}
static int tiff_warning(TIFF *tiff, void *data, const char *module, const char *format, va_list args)
{
    // 손상 경고를 정상 픽셀로 숨기지 않는다. 특수 metadata 경고도 현재는 명시적으로 거부한다.
    return tiff_error(tiff, data, module, format, args);
}
static void destroy(vi_reader *base)
{
    tiff_reader *reader = (tiff_reader *)base;
    if (reader->tiff) TIFFClose(reader->tiff);
    free(reader->band);
    free(reader->tile);
}

static int load_band(tiff_reader *reader, uint32_t top, const int32_t *cancel)
{
    vi_reader *base = &reader->base;
    uint32_t height = (uint32_t)base->info.height;
    uint32_t width = (uint32_t)base->info.width;
    uint32_t rows = reader->tile_height < height - top ? reader->tile_height : height - top;
    size_t bpp = (size_t)reader->samples * (reader->bits / 8);
    for (uint32_t left = 0; left < width; left += reader->tile_width)
    {
        if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "TIFF decode cancelled.");
        uint32_t tile = TIFFComputeTile(reader->tiff, left, top, 0, 0);
        tmsize_t length = TIFFReadEncodedTile(reader->tiff, tile, reader->tile, -1);
        if (length < 0 || (uint64_t)length < (uint64_t)reader->tile_stride * rows || base->error.code)
            return base->error.code ? base->error.code : vi_fail(base, VI_INVALID, "Incomplete TIFF tile.");
        uint32_t columns = reader->tile_width < width - left ? reader->tile_width : width - left;
        for (uint32_t row = 0; row < rows; row++)
            memcpy(reader->band + (size_t)row * reader->input_stride + (size_t)left * bpp, reader->tile + (size_t)row * reader->tile_stride, (size_t)columns * bpp);
    }
    reader->band_top = top;
    return VI_OK;
}

static int read_rows(vi_reader *base, uint32_t count, uint8_t *output, const int32_t *cancel)
{
    tiff_reader *reader = (tiff_reader *)base;
    size_t output_stride = (size_t)base->info.width * (base->info.kind == VI_GRAY8 ? 1 : base->info.kind == VI_GRAY16 ? 2 : 4);
    for (uint32_t index = 0; index < count; index++)
    {
        if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "TIFF decode cancelled.");
        uint32_t row = base->next_row;
        uint8_t *input = reader->band;
        if (reader->tile_height)
        {
            uint32_t top = row - row % reader->tile_height;
            if (reader->band_top != top && load_band(reader, top, cancel)) return base->error.code;
            input += (size_t)(row - top) * reader->input_stride;
        }
        else if (TIFFReadScanline(reader->tiff, input, row, 0) < 0 || base->error.code)
            return base->error.code ? base->error.code : vi_fail(base, VI_INVALID, "Incomplete TIFF scanline.");
        if (reader->samples == 3)
        {
            for (int32_t x = 0; x < base->info.width; x++)
            { memcpy(output + (size_t)x * 4, input + (size_t)x * 3, 3); output[(size_t)x * 4 + 3] = 255; }
        }
        else memcpy(output, input, output_stride);
        if (reader->photo == PHOTOMETRIC_MINISWHITE)
            for (size_t x = 0; x < output_stride; x++) output[x] ^= 255;
        output += output_stride;
        base->next_row++;
    }
    return VI_OK;
}

int vi_tiff_open(FILE *file, uint64_t budget, vi_reader **result, vi_error *error)
{
    tiff_reader *reader = calloc(1, sizeof(*reader));
    if (!reader) { fclose(file); return error->code = VI_BUDGET; }
    vi_reader *base = &reader->base;
    *result = base;
    base->file = file;
    base->budget = budget;
    base->destroy = destroy;
    base->read = read_rows;
    TIFFOpenOptions *options = TIFFOpenOptionsAlloc();
    if (!options) { vi_fail(base, VI_BUDGET, "Cannot allocate TIFF options."); goto failed; }
    TIFFOpenOptionsSetMaxSingleMemAlloc(options, (tmsize_t)budget);
    TIFFOpenOptionsSetMaxCumulatedMemAlloc(options, (tmsize_t)budget);
    TIFFOpenOptionsSetErrorHandlerExtR(options, tiff_error, base);
    TIFFOpenOptionsSetWarningHandlerExtR(options, tiff_warning, base);
    reader->tiff = TIFFClientOpenExt("image", "rm", base, read_file, write_file, seek_file, close_file, size_file, map_file, unmap_file, options);
    TIFFOpenOptionsFree(options);
    if (!reader->tiff || base->error.code) { if (!base->error.code) vi_fail(base, VI_INVALID, "Invalid TIFF header."); goto failed; }
    uint32_t width = 0, height = 0;
    uint16_t planar, sample_format, compression;
    TIFFGetField(reader->tiff, TIFFTAG_IMAGEWIDTH, &width);
    TIFFGetField(reader->tiff, TIFFTAG_IMAGELENGTH, &height);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_BITSPERSAMPLE, &reader->bits);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_SAMPLESPERPIXEL, &reader->samples);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_PLANARCONFIG, &planar);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_SAMPLEFORMAT, &sample_format);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_COMPRESSION, &compression);
    TIFFGetFieldDefaulted(reader->tiff, TIFFTAG_PHOTOMETRIC, &reader->photo);
    if (!width || !height || width >= INT32_MAX / 4 || height >= INT32_MAX || planar != PLANARCONFIG_CONTIG || sample_format != SAMPLEFORMAT_UINT ||
        !((reader->samples == 1 && (reader->bits == 8 || reader->bits == 16) && reader->photo <= PHOTOMETRIC_MINISBLACK) ||
          ((reader->samples == 3 || reader->samples == 4) && reader->bits == 8 && reader->photo == PHOTOMETRIC_RGB)) ||
        !(compression == COMPRESSION_NONE || compression == COMPRESSION_LZW || compression == COMPRESSION_ADOBE_DEFLATE || compression == COMPRESSION_DEFLATE || compression == COMPRESSION_PACKBITS))
    { vi_fail(base, VI_UNSUPPORTED, "Unsupported TIFF pixel layout or compression."); goto failed; }
    if (reader->samples == 4)
    {
        uint16_t count = 0, *extras = NULL;
        TIFFGetField(reader->tiff, TIFFTAG_EXTRASAMPLES, &count, &extras);
        if (count != 1 || extras[0] != EXTRASAMPLE_UNASSALPHA)
        { vi_fail(base, VI_UNSUPPORTED, "TIFF RGBA requires unassociated alpha."); goto failed; }
    }
    base->info = (vi_info){ (int32_t)width, (int32_t)height, reader->samples == 1 ? (reader->bits == 8 ? VI_GRAY8 : VI_GRAY16) : VI_RGBA32, 0 };
    reader->input_stride = (size_t)width * reader->samples * (reader->bits / 8);
    uint64_t band_size = reader->input_stride, tile_size = 0;
    if (TIFFIsTiled(reader->tiff))
    {
        TIFFGetField(reader->tiff, TIFFTAG_TILEWIDTH, &reader->tile_width);
        TIFFGetField(reader->tiff, TIFFTAG_TILELENGTH, &reader->tile_height);
        tile_size = TIFFTileSize64(reader->tiff);
        reader->tile_stride = (size_t)TIFFTileRowSize64(reader->tiff);
        band_size *= reader->tile_height;
        reader->band_top = UINT32_MAX;
        if (!reader->tile_width || !reader->tile_height || !tile_size ||
            reader->tile_stride != (uint64_t)reader->tile_width * reader->samples * (reader->bits / 8) ||
            tile_size < (uint64_t)reader->tile_stride * reader->tile_height)
        { vi_fail(base, VI_INVALID, "Invalid TIFF tile layout."); goto failed; }
    }
    else if (TIFFScanlineSize64(reader->tiff) != reader->input_stride)
    { vi_fail(base, VI_UNSUPPORTED, "Unsupported TIFF scanline layout."); goto failed; }
    if (band_size > budget || tile_size > budget - band_size)
    { vi_fail(base, VI_BUDGET, "TIFF row band exceeds decoder memory budget."); goto failed; }
    reader->band = malloc((size_t)band_size);
    if (tile_size) reader->tile = malloc((size_t)tile_size);
    if (!reader->band || (tile_size && !reader->tile))
    { vi_fail(base, VI_BUDGET, "Cannot allocate TIFF row band."); goto failed; }
    return VI_OK;
failed:
    *error = base->error;
    return error->code;
}
