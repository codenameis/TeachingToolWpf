#include "reader.h"
#include <png.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <stddef.h>

typedef struct
{
    vi_reader base;
    png_structp png;
    png_infop info;
    FILE *scratch;
    uint8_t *row;
    size_t stride;
    uint64_t allocated;
} png_reader;
// x64 malloc의 정렬을 유지하도록 픽셀 시작점을16바이트 뒤에 둔다.
typedef struct { size_t size, padding; } allocation_header;

static png_voidp allocate(png_structp png, png_alloc_size_t size)
{
    png_reader *reader = png_get_mem_ptr(png);
    if (size > SIZE_MAX - sizeof(allocation_header) || size + sizeof(allocation_header) > reader->base.budget - reader->allocated)
    { vi_fail(&reader->base, VI_BUDGET, "PNG allocation exceeds decoder budget."); return NULL; }
    allocation_header *header = malloc(sizeof(*header) + size);
    if (!header) { vi_fail(&reader->base, VI_BUDGET, "Cannot allocate PNG buffer."); return NULL; }
    header->size = size + sizeof(*header);
    reader->allocated += header->size;
    return header + 1;
}
static void release(png_structp png, png_voidp block)
{
    if (!block) return;
    png_reader *reader = png_get_mem_ptr(png);
    allocation_header *header = (allocation_header *)block - 1;
    reader->allocated -= header->size;
    free(header);
}
static void png_failure(png_structp png, png_const_charp message)
{
    vi_reader *base = png_get_error_ptr(png);
    if (!base->error.code) vi_fail(base, VI_INVALID, message);
    png_longjmp(png, 1);
}
static void destroy(vi_reader *base)
{
    png_reader *reader = (png_reader *)base;
    if (reader->png) png_destroy_read_struct(&reader->png, &reader->info, NULL);
    if (reader->scratch) fclose(reader->scratch);
    free(reader->row);
}

static int read_rows(vi_reader *base, uint32_t count, uint8_t *output, const int32_t *cancel)
{
    png_reader *reader = (png_reader *)base;
    // longjmp는 이 native 호출 안에서만 복구한다. managed frame을 건너지 않는다.
    if (setjmp(png_jmpbuf(reader->png))) return base->error.code;
    for (uint32_t row = 0; row < count; row++)
    {
        if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "PNG decode cancelled.");
        if (reader->scratch)
        {
            if (fread(output, 1, reader->stride, reader->scratch) != reader->stride)
                return vi_fail(base, VI_IO, "Cannot read PNG scratch file.");
        }
        else png_read_row(reader->png, output, NULL);
        output += reader->stride;
        base->next_row++;
    }
    if (!reader->scratch && base->next_row == (uint32_t)base->info.height) png_read_end(reader->png, NULL);
    return VI_OK;
}

static int read_interlaced(png_reader *reader, int passes, const int32_t *cancel)
{
    vi_reader *base = &reader->base;
    if (reader->stride > base->budget - reader->allocated)
        return vi_fail(base, VI_BUDGET, "PNG scratch row exceeds decoder budget.");
    reader->row = calloc(1, reader->stride);
    reader->scratch = vi_scratch();
    if (!reader->row || !reader->scratch) return vi_fail(base, VI_IO, "Cannot create PNG scratch file.");
    reader->allocated += reader->stride;
    for (int pass = 0; pass < passes; pass++)
    {
        for (int32_t row = 0; row < base->info.height; row++)
        {
            if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "PNG interlace decode cancelled.");
            int64_t offset = (int64_t)row * (int64_t)reader->stride;
            if (pass)
            {
                if (vi_file_seek(reader->scratch, offset) || fread(reader->row, 1, reader->stride, reader->scratch) != reader->stride)
                    return vi_fail(base, VI_IO, "Cannot read PNG interlace pass.");
            }
            else memset(reader->row, 0, reader->stride);
            png_read_row(reader->png, reader->row, NULL);
            if (vi_file_seek(reader->scratch, offset) || fwrite(reader->row, 1, reader->stride, reader->scratch) != reader->stride)
                return vi_fail(base, VI_IO, "Cannot write PNG interlace pass (disk full?).");
        }
    }
    png_read_end(reader->png, NULL);
    if (fflush(reader->scratch) || vi_file_seek(reader->scratch, 0)) return vi_fail(base, VI_IO, "Cannot finish PNG scratch file.");
    reader->allocated -= reader->stride;
    free(reader->row);
    reader->row = NULL;
    return VI_OK;
}

int vi_png_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **result, vi_error *error)
{
    png_reader *reader = calloc(1, sizeof(*reader));
    if (!reader) { fclose(file); return error->code = VI_BUDGET; }
    vi_reader *base = &reader->base;
    *result = base;
    base->file = file;
    base->budget = budget;
    base->destroy = destroy;
    base->read = read_rows;
    reader->png = png_create_read_struct_2(PNG_LIBPNG_VER_STRING, base, png_failure, png_failure, reader, allocate, release);
    if (!reader->png) { vi_fail(base, VI_BUDGET, "Cannot create PNG reader."); goto failed; }
    if (setjmp(png_jmpbuf(reader->png))) goto failed;
    reader->info = png_create_info_struct(reader->png);
    if (!reader->info) { vi_fail(base, VI_BUDGET, "Cannot create PNG metadata."); goto failed; }
    png_set_user_limits(reader->png, INT32_MAX / 4 - 1, INT32_MAX - 1);
    png_set_chunk_malloc_max(reader->png, (png_alloc_size_t)budget);
    png_set_crc_action(reader->png, PNG_CRC_ERROR_QUIT, PNG_CRC_ERROR_QUIT);
    png_init_io(reader->png, file);
    png_read_info(reader->png, reader->info);
    png_uint_32 width = png_get_image_width(reader->png, reader->info);
    png_uint_32 height = png_get_image_height(reader->png, reader->info);
    int bits = png_get_bit_depth(reader->png, reader->info);
    int color = png_get_color_type(reader->png, reader->info);
    int transparency = png_get_valid(reader->png, reader->info, PNG_INFO_tRNS) != 0;
    if (bits == 16 && (color != PNG_COLOR_TYPE_GRAY || transparency))
    { vi_fail(base, VI_UNSUPPORTED, "PNG16 supports only opaque grayscale."); goto failed; }
    int gray = color == PNG_COLOR_TYPE_GRAY && !transparency;
    if (color == PNG_COLOR_TYPE_PALETTE) png_set_palette_to_rgb(reader->png);
    if (color == PNG_COLOR_TYPE_GRAY && bits < 8) png_set_expand_gray_1_2_4_to_8(reader->png);
    if (transparency) png_set_tRNS_to_alpha(reader->png);
    if (!gray)
    {
        if (color == PNG_COLOR_TYPE_GRAY || color == PNG_COLOR_TYPE_GRAY_ALPHA) png_set_gray_to_rgb(reader->png);
        if (!(color & PNG_COLOR_MASK_ALPHA) && !transparency) png_set_add_alpha(reader->png, 255, PNG_FILLER_AFTER);
    }
    if (bits == 16) png_set_swap(reader->png);
    int passes = png_set_interlace_handling(reader->png);
    png_read_update_info(reader->png, reader->info);
    base->info = (vi_info){ (int32_t)width, (int32_t)height, gray ? (bits == 16 ? VI_GRAY16 : VI_GRAY8) : VI_RGBA32, 0 };
    reader->stride = png_get_rowbytes(reader->png, reader->info);
    if (reader->stride != (size_t)width * (gray ? (bits == 16 ? 2 : 1) : 4))
    { vi_fail(base, VI_INVALID, "Unexpected PNG row layout."); goto failed; }
    if (passes > 1 && read_interlaced(reader, passes, cancel)) goto failed;
    return VI_OK;
failed:
    *error = base->error;
    return error->code;
}
