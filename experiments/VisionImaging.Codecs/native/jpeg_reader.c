#include "reader.h"
#include <jpeglib.h>
#include <jerror.h>
#include <setjmp.h>
#include <stdlib.h>
#include <limits.h>

typedef struct jpeg_reader jpeg_reader;
#ifdef _MSC_VER
// Windows jmp_buf는16바이트 정렬을 요구한다. 내부 오류 객체의 정렬 padding은 의도적이다.
#pragma warning(push)
#pragma warning(disable : 4324)
#endif
typedef struct
{
    struct jpeg_error_mgr manager;
    jmp_buf recovery;
    jpeg_reader *owner;
} jpeg_errors;
#ifdef _MSC_VER
#pragma warning(pop)
#endif
struct jpeg_reader
{
    vi_reader base;
    struct jpeg_decompress_struct jpeg;
    jpeg_errors errors;
    struct jpeg_source_mgr source;
    const int32_t *cancel;
    JOCTET input[65536];
};

static void jpeg_failure(j_common_ptr common)
{
    jpeg_errors *errors = (jpeg_errors *)common->err;
    vi_reader *base = &errors->owner->base;
    if (!base->error.code)
    {
        char message[JMSG_LENGTH_MAX];
        (*common->err->format_message)(common, message);
        int code = common->err->msg_code;
        vi_fail(base, code == JERR_NO_BACKING_STORE || code == JERR_OUT_OF_MEMORY ? VI_BUDGET : VI_INVALID, message);
    }
    longjmp(errors->recovery, 1);
}
static void jpeg_message(j_common_ptr common, int level)
{
    if (level < 0) jpeg_failure(common);
}
static void init_source(j_decompress_ptr jpeg) { (void)jpeg; }
static void term_source(j_decompress_ptr jpeg) { (void)jpeg; }
static boolean fill_source(j_decompress_ptr jpeg)
{
    jpeg_reader *reader = jpeg->client_data;
    if (vi_cancelled(reader->cancel))
    {
        vi_fail(&reader->base, VI_CANCELLED, "JPEG decode cancelled.");
        ERREXIT(jpeg, JERR_FILE_READ);
    }
    size_t size = fread(reader->input, 1, sizeof(reader->input), reader->base.file);
    if (!size) ERREXIT(jpeg, JERR_INPUT_EOF);
    reader->source.next_input_byte = reader->input;
    reader->source.bytes_in_buffer = size;
    return TRUE;
}
static void skip_source(j_decompress_ptr jpeg, long count)
{
    if (count <= 0) return;
    while ((size_t)count > jpeg->src->bytes_in_buffer)
    {
        count -= (long)jpeg->src->bytes_in_buffer;
        fill_source(jpeg);
    }
    jpeg->src->next_input_byte += (size_t)count;
    jpeg->src->bytes_in_buffer -= (size_t)count;
}
static void destroy(vi_reader *base)
{
    jpeg_reader *reader = (jpeg_reader *)base;
    if (reader->jpeg.mem) jpeg_destroy_decompress(&reader->jpeg);
}
static int read_rows(vi_reader *base, uint32_t count, uint8_t *output, const int32_t *cancel)
{
    jpeg_reader *reader = (jpeg_reader *)base;
    reader->cancel = cancel;
    if (setjmp(reader->errors.recovery)) return base->error.code;
    size_t stride = (size_t)base->info.width * (base->info.kind == VI_GRAY8 ? 1 : 4);
    for (uint32_t index = 0; index < count; index++)
    {
        if (vi_cancelled(cancel)) return vi_fail(base, VI_CANCELLED, "JPEG decode cancelled.");
        JSAMPROW row = output;
        if (jpeg_read_scanlines(&reader->jpeg, &row, 1) != 1) return vi_fail(base, VI_INVALID, "Incomplete JPEG row.");
        output += stride;
        base->next_row++;
    }
    if (base->next_row == (uint32_t)base->info.height && !jpeg_finish_decompress(&reader->jpeg))
        return vi_fail(base, VI_INVALID, "Incomplete JPEG image.");
    return VI_OK;
}

int vi_jpeg_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **result, vi_error *error)
{
    jpeg_reader *reader = calloc(1, sizeof(*reader));
    if (!reader) { fclose(file); return error->code = VI_BUDGET; }
    vi_reader *base = &reader->base;
    *result = base;
    base->file = file;
    base->budget = budget;
    base->destroy = destroy;
    base->read = read_rows;
    reader->cancel = cancel;
    reader->errors.owner = reader;
    reader->jpeg.err = jpeg_std_error(&reader->errors.manager);
    reader->errors.manager.error_exit = jpeg_failure;
    reader->errors.manager.emit_message = jpeg_message;
    if (setjmp(reader->errors.recovery)) goto failed;
    jpeg_create_decompress(&reader->jpeg);
    reader->jpeg.client_data = reader;
    reader->source.init_source = init_source;
    reader->source.fill_input_buffer = fill_source;
    reader->source.skip_input_data = skip_source;
    reader->source.resync_to_restart = jpeg_resync_to_restart;
    reader->source.term_source = term_source;
    reader->jpeg.src = &reader->source;
    if (jpeg_read_header(&reader->jpeg, TRUE) != JPEG_HEADER_OK || reader->jpeg.data_precision != 8 ||
        !(reader->jpeg.jpeg_color_space == JCS_GRAYSCALE || reader->jpeg.jpeg_color_space == JCS_YCbCr || reader->jpeg.jpeg_color_space == JCS_RGB))
    { vi_fail(base, VI_UNSUPPORTED, "JPEG supports8bit grayscale/RGB only."); goto failed; }
    if (!reader->jpeg.image_width || !reader->jpeg.image_height)
    { vi_fail(base, VI_INVALID, "Invalid JPEG size."); goto failed; }
    uint64_t required = sizeof(*reader) + 4 * 1024 * 1024 + (uint64_t)reader->jpeg.image_width * 64;
    if (jpeg_has_multiple_scans(&reader->jpeg))
    {
        uint64_t across = ((uint64_t)reader->jpeg.image_width + reader->jpeg.max_h_samp_factor * 8 - 1) / (reader->jpeg.max_h_samp_factor * 8);
        uint64_t down = ((uint64_t)reader->jpeg.image_height + reader->jpeg.max_v_samp_factor * 8 - 1) / (reader->jpeg.max_v_samp_factor * 8);
        for (int index = 0; index < reader->jpeg.num_components; index++)
            required += across * down * reader->jpeg.comp_info[index].h_samp_factor * reader->jpeg.comp_info[index].v_samp_factor * 64 * sizeof(JCOEF);
    }
    if (required > budget)
    { vi_fail(base, VI_BUDGET, "JPEG coefficient/work buffers exceed decoder budget."); goto failed; }
    reader->jpeg.mem->max_memory_to_use = (long)(budget < LONG_MAX ? budget : LONG_MAX);
    reader->jpeg.out_color_space = reader->jpeg.jpeg_color_space == JCS_GRAYSCALE ? JCS_GRAYSCALE : JCS_EXT_RGBA;
    if (!jpeg_start_decompress(&reader->jpeg))
    { vi_fail(base, VI_INVALID, "JPEG start suspended."); goto failed; }
    base->info = (vi_info){ (int32_t)reader->jpeg.output_width, (int32_t)reader->jpeg.output_height,
        reader->jpeg.out_color_space == JCS_GRAYSCALE ? VI_GRAY8 : VI_RGBA32, 0 };
    return VI_OK;
failed:
    *error = base->error;
    return error->code;
}
