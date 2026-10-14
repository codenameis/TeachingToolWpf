#include "reader.h"
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#endif

int vi_abi_version(void) { return 1; }

int vi_cancelled(const int32_t *cancel)
{
    if (!cancel) return 0;
#ifdef _WIN32
    return InterlockedCompareExchange((volatile LONG *)cancel, 0, 0) != 0;
#else
    return __atomic_load_n(cancel, __ATOMIC_ACQUIRE) != 0;
#endif
}

int vi_fail(vi_reader *reader, int status, const char *message)
{
    reader->error.code = status;
    snprintf(reader->error.message, sizeof(reader->error.message), "%s", message);
    return status;
}

FILE *vi_fopen(const char *path)
{
#ifdef _WIN32
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
    if (!count) return NULL;
    wchar_t *wide = malloc((size_t)count * sizeof(wchar_t));
    if (!wide) return NULL;
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide, count);
    FILE *file = _wfopen(wide, L"rb");
    free(wide);
    return file;
#else
    return fopen(path, "rb");
#endif
}

int vi_file_seek(FILE *file, int64_t offset)
{
#ifdef _WIN32
    return _fseeki64(file, offset, SEEK_SET);
#else
    return fseeko(file, offset, SEEK_SET);
#endif
}

FILE *vi_scratch(void)
{
#ifdef _WIN32
    wchar_t directory[MAX_PATH + 1], path[MAX_PATH + 1];
    DWORD length = GetTempPathW(MAX_PATH, directory);
    if (!length || length >= MAX_PATH || !GetTempFileNameW(directory, L"vic", 0, path)) return NULL;
    HANDLE file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, NULL);
    if (file == INVALID_HANDLE_VALUE) { DeleteFileW(path); return NULL; }
    int descriptor = _open_osfhandle((intptr_t)file, _O_BINARY | _O_RDWR);
    if (descriptor < 0) { CloseHandle(file); return NULL; }
    FILE *stream = _fdopen(descriptor, "w+b");
    if (!stream) _close(descriptor);
    return stream;
#else
    return tmpfile();
#endif
}

int vi_open(const char *path, uint64_t budget, const int32_t *cancel, vi_reader **reader, vi_info *info, vi_error *error)
{
    if (!reader || !info || !error) return VI_INVALID;
    *reader = NULL;
    memset(info, 0, sizeof(*info));
    memset(error, 0, sizeof(*error));
    if (!path || budget < 1024 || budget > INT64_MAX) return error->code = VI_INVALID;
    if (vi_cancelled(cancel)) return error->code = VI_CANCELLED;
    FILE *file = vi_fopen(path);
    if (!file)
    {
        snprintf(error->message, sizeof(error->message), "Cannot open image file.");
        return error->code = VI_IO;
    }
    unsigned char signature[12] = { 0 };
    size_t size = fread(signature, 1, sizeof(signature), file);
    rewind(file);
    int status;
    if (size >= 4 && ((signature[0] == 'I' && signature[1] == 'I') || (signature[0] == 'M' && signature[1] == 'M')))
        status = vi_tiff_open(file, budget, reader, error);
    else if (size >= 8 && !memcmp(signature, "\x89PNG\r\n\x1a\n", 8))
        status = vi_png_open(file, budget, cancel, reader, error);
    else if (size >= 2 && signature[0] == 255 && signature[1] == 216)
        status = vi_jpeg_open(file, budget, cancel, reader, error);
    else if (size == 12 && !memcmp(signature, "RIFF", 4) && !memcmp(signature + 8, "WEBP", 4))
        status = vi_webp_open(file, budget, cancel, reader, error);
    else
    {
        fclose(file);
        snprintf(error->message, sizeof(error->message), "Unsupported image signature.");
        status = error->code = VI_UNSUPPORTED;
    }
    if (status == VI_OK && vi_cancelled(cancel)) status = error->code = VI_CANCELLED;
    if (status != VI_OK) { vi_close(*reader); *reader = NULL; }
    else *info = (*reader)->info;
    return status;
}

int vi_read_rows(vi_reader *reader, int32_t count, uint8_t *pixels, uint64_t length, const int32_t *cancel, vi_error *error)
{
    if (!reader || !error) return VI_INVALID;
    uint64_t stride = (uint64_t)reader->info.width * (reader->info.kind == VI_GRAY8 ? 1 : reader->info.kind == VI_GRAY16 ? 2 : 4);
    int status;
    if (reader->error.code != VI_OK) status = reader->error.code;
    else if (count <= 0 || !pixels || (uint32_t)count > (uint32_t)reader->info.height - reader->next_row || length < stride * (uint32_t)count)
        status = vi_fail(reader, VI_INVALID, "Invalid sequential row request.");
    else if (vi_cancelled(cancel)) status = vi_fail(reader, VI_CANCELLED, "Decode cancelled.");
    else status = reader->read(reader, (uint32_t)count, pixels, cancel);
    *error = reader->error;
    return status;
}

void vi_close(vi_reader *reader)
{
    if (!reader) return;
    reader->destroy(reader);
    if (reader->file) fclose(reader->file);
    free(reader);
}
