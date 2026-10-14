#ifndef VISION_IMAGE_READER_H
#define VISION_IMAGE_READER_H
#include <stdint.h>
#include <stdio.h>

#if defined(_WIN32)
#define VI_API __declspec(dllexport)
#else
#define VI_API __attribute__((visibility("default")))
#endif

enum vi_status { VI_OK, VI_INVALID, VI_UNSUPPORTED, VI_IO, VI_CANCELLED, VI_BUDGET };
enum vi_pixel { VI_GRAY8, VI_GRAY16, VI_RGBA32 };
typedef struct vi_info { int32_t width, height, kind, reserved; } vi_info;
typedef struct vi_error { int32_t code; char message[508]; } vi_error;
typedef struct vi_reader vi_reader;
struct vi_reader
{
    FILE *file;
    vi_info info;
    uint32_t next_row;
    uint64_t budget;
    vi_error error;
    int (*read)(vi_reader *, uint32_t, uint8_t *, const int32_t *);
    void (*destroy)(vi_reader *);
};

VI_API int vi_abi_version(void);
VI_API int vi_open(const char *path, uint64_t budget, const int32_t *cancel, vi_reader **reader, vi_info *info, vi_error *error);
VI_API int vi_read_rows(vi_reader *reader, int32_t count, uint8_t *pixels, uint64_t length, const int32_t *cancel, vi_error *error);
VI_API void vi_close(vi_reader *reader);

int vi_fail(vi_reader *reader, int status, const char *message);
int vi_cancelled(const int32_t *cancel);
FILE *vi_fopen(const char *path);
FILE *vi_scratch(void);
int vi_file_seek(FILE *file, int64_t offset);
int vi_tiff_open(FILE *file, uint64_t budget, vi_reader **reader, vi_error *error);
int vi_png_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **reader, vi_error *error);
int vi_jpeg_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **reader, vi_error *error);
int vi_webp_open(FILE *file, uint64_t budget, const int32_t *cancel, vi_reader **reader, vi_error *error);
#endif
