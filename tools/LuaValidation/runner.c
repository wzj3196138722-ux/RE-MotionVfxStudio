#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"

static char *utf8(const wchar_t *value) {
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, NULL, 0, NULL, NULL);
    char *result = (char *)malloc(size);
    if (result) WideCharToMultiByte(CP_UTF8, 0, value, -1, result, size, NULL, NULL);
    return result;
}

int wmain(int argc, wchar_t **argv) {
    int parse_only = argc > 1 && wcscmp(argv[1], L"-p") == 0;
    int index = parse_only ? 2 : 1;
    if (argc <= index) { fprintf(stderr, "Usage: lua-runner [-p] script [args]\n"); return 2; }
    FILE *file = _wfopen(argv[index], L"rb");
    if (!file) { perror("open script"); return 2; }
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    rewind(file);
    if (length < 0) { fclose(file); return 2; }
    char *data = (char *)malloc((size_t)length + 1);
    if (!data) { fclose(file); return 2; }
    size_t read = fread(data, 1, (size_t)length, file);
    fclose(file);
    if (read != (size_t)length) { free(data); return 2; }
    lua_State *state = luaL_newstate();
    if (!state) { free(data); return 2; }
    luaL_openlibs(state);
    lua_createtable(state, argc - index - 1, 1);
    for (int i = index; i < argc; ++i) {
        char *value = utf8(argv[i]);
        lua_pushstring(state, value);
        lua_rawseti(state, -2, i - index);
        free(value);
    }
    lua_setglobal(state, "arg");
    char *name = utf8(argv[index]);
    const char *code = data;
    size_t size = (size_t)length;
    if (size >= 3 && (unsigned char)data[0] == 0xef && (unsigned char)data[1] == 0xbb && (unsigned char)data[2] == 0xbf) { code += 3; size -= 3; }
    int status = luaL_loadbufferx(state, code, size, name, "t");
    if (status == LUA_OK && !parse_only) status = lua_pcall(state, 0, LUA_MULTRET, 0);
    if (status != LUA_OK) fprintf(stderr, "%s\n", lua_tostring(state, -1));
    free(name);
    free(data);
    lua_close(state);
    return status == LUA_OK ? 0 : 1;
}
