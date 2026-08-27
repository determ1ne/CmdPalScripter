const getCurrentProcessId = ffi.bind(
    "kernel32.dll",
    "GetCurrentProcessId",
    "winapi u32()"
);

getCurrentProcessId.Invoke();
