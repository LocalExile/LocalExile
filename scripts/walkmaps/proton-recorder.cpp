#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include "frida-gumjs.h"
#include <stdint.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <errno.h>
#include <limits.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern "C" __attribute__((visibility("default")))
int localexile_cell_heights(void *map, void *fn, int w, int h, short *out) {
    typedef int (__attribute__((ms_abi)) *height_fn)(void *, int *);
    height_fn height = (height_fn)fn;
    int cell[2], clipped = 0;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
        cell[0] = x; cell[1] = y;
        int v = height(map, cell);
        if (v < -32768 || v > 32767) { clipped++; v = v < 0 ? -32768 : 32767; }
        out[y*w+x] = (short)v;
    }
    return clipped;
}

static int output_socket = -1;

static bool send_all(const void *data, size_t size) {
    const char *cursor = (const char *)data;
    while (size) {
        ssize_t n = send(output_socket, cursor, size, MSG_NOSIGNAL);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return false;
        cursor += n; size -= (size_t)n;
    }
    return true;
}

static void on_message(const gchar *message, GBytes *data, gpointer user) {
    (void)user;
    gsize size = 0;
    const void *bytes = data ? g_bytes_get_data(data, &size) : NULL;
    size_t length = strlen(message);
    if (output_socket < 0 || length > 64*1024*1024 || size > 256*1024*1024) return;
    uint32_t header[] = { (uint32_t)length, (uint32_t)size };
    if (!send_all(header, sizeof header) || !send_all(message, length) || (size && !send_all(bytes, size))) {
        close(output_socket); output_socket = -1;
        fprintf(stderr, "[walkmaps-proton] recorder disconnected; client continues without capture output\n");
    }
}

static int connect_output(void) {
    const char *path = getenv("LOCALEXILE_CAPTURE_SOCKET");
    if (!path || strlen(path) >= sizeof(((struct sockaddr_un *)0)->sun_path)) return -1;
    struct sockaddr_un address = {};
    address.sun_family = AF_UNIX;
    strcpy(address.sun_path, path);
    for (int i = 0; i < 300; i++) {
        int fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
        if (fd < 0) return -1;
        if (!connect(fd, (struct sockaddr *)&address, sizeof address)) {
            struct timeval limit = { 2, 0 };
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &limit, sizeof limit);
            return fd;
        }
        close(fd);
        usleep(100000);
    }
    return -1;
}

static void *load_recorder(void *unused) {
    (void)unused;
    const char *image = getenv("LOCALEXILE_CAPTURE_IMAGE");
    const char *agent = getenv("LOCALEXILE_CAPTURE_AGENT");
    int mem = open("/proc/self/mem", O_RDONLY);
    if (mem < 0) return NULL;
    unsigned char header[4096];
    const unsigned char expected[] = {0x48,0x8b,0x08,0x48,0x89,0x4d,0xc8,0x48,0x8d,0x50,0x08,0x48,0x8d,0x4d,0xd0,0x48};
    uint32_t pe = 0, image_size = 0;
    uintptr_t base = 0;
    for (int n = 0; n < 600; n++) {
        FILE *maps = fopen("/proc/self/maps", "r");
        char line[PATH_MAX + 256];
        unsigned long start = 0, end = 0, offset = 0;
        char access[5];
        base = 0; image_size = 0;
        if (maps) {
            while (fgets(line, sizeof line, maps)) {
                char *path = strchr(line, '/');
                if (path) path[strcspn(path, "\n")] = 0;
                if (path && !strcmp(path, image) && sscanf(line, "%lx-%lx %4s %lx", &start, &end, access, &offset) == 4 && offset == 0) {
                    base = (uintptr_t)start;
                    break;
                }
            }
            fclose(maps);
        }
        if (base && pread(mem, header, sizeof header, (off_t)base) == sizeof header && header[0] == 'M' && header[1] == 'Z') {
            memcpy(&pe, header + 0x3c, 4);
            if (pe < sizeof header - 84 && !memcmp(header + pe, "PE\0\0", 4)) {
                uint32_t candidate = 0;
                unsigned char code[sizeof expected];
                memcpy(&candidate, header + pe + 24 + 56, 4);
                if (candidate > 0x2000000 && candidate < 0x10000000 &&
                    pread(mem, code, sizeof code, (off_t)(base + 0x10e00bd)) == sizeof code &&
                    !memcmp(code, expected, sizeof code)) { image_size = candidate; break; }
            }
        }
        usleep(100000);
    }
    close(mem);
    if (!image_size) { fprintf(stderr, "[walkmaps-proton] no supported PE image mapped\n"); return NULL; }
    output_socket = connect_output();
    if (output_socket < 0) { fprintf(stderr, "[walkmaps-proton] no local recorder receiver; capture disabled\n"); return NULL; }
    gchar *body = NULL;
    if (!g_file_get_contents(agent, &body, NULL, NULL)) { fprintf(stderr, "[walkmaps-proton] cannot read capture script\n"); close(output_socket); output_socket = -1; return NULL; }
    gchar *source = g_strdup_printf("globalThis.protonRecorder={base:'0x%lx',size:%u,height:'0x%lx'};\n%s",
        (unsigned long)base, image_size, (unsigned long)(uintptr_t)localexile_cell_heights, body);
    g_free(body);
    GMainContext *context = g_main_context_new();
    g_main_context_push_thread_default(context);
    GError *error = NULL;
    GumScript *script = gum_script_backend_create_sync(gum_script_backend_obtain_qjs(), "walkmaps-proton", source, NULL, NULL, &error);
    g_free(source);
    if (!script) { fprintf(stderr, "[walkmaps-proton] %s\n", error->message); g_error_free(error); close(output_socket); output_socket = -1; g_main_context_pop_thread_default(context); g_main_context_unref(context); return NULL; }
    gum_script_set_message_handler(script, on_message, NULL, NULL);
    gum_script_load_sync(script, NULL);
    fprintf(stderr, "[walkmaps-proton] capture script loaded in pid %ld\n", (long)getpid());
    for (;;) g_main_context_iteration(context, TRUE);
    return NULL;
}

__attribute__((constructor)) static void initialize(void) {
    const char *image = getenv("LOCALEXILE_CAPTURE_IMAGE");
    const char *gadget = getenv("LOCALEXILE_CAPTURE_AGENT");
    if (!image || !gadget) return;
    char exe[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", exe, sizeof exe-1);
    if (n < 0) return;
    exe[n] = 0;
    const char *leaf = strrchr(exe, '/'); leaf = leaf ? leaf + 1 : exe;
    if (!strstr(leaf, "wine")) return;
    FILE *cmd = fopen("/proc/self/cmdline", "r");
    if (!cmd) return;
    char args[16384];
    size_t size = fread(args, 1, sizeof args-1, cmd); fclose(cmd); args[size] = 0;
    int target = 0;
    for (size_t i = 0; i < size; i += strlen(args+i)+1) {
        const char *name = args+i;
        const char *slash = strrchr(name, '/');
        const char *backslash = strrchr(name, '\\');
        if (slash) name = slash+1;
        if (backslash && backslash >= name) name = backslash+1;
        size_t length = strlen(name);
        if (length >= 4 && !strcasecmp(name+length-4, ".exe")) {
            target = !strcmp(name, "Game.patched.exe");
            break;
        }
    }
    if (!target) return;
    gum_init_embedded();
    // Wine's preloader changes the main image metadata. Prime Gum's ELF/libc discovery before that happens.
    (void)gum_process_get_libc_module();
    pthread_t worker;
    if (!pthread_create(&worker, NULL, load_recorder, NULL)) pthread_detach(worker);
}
