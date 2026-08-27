const powerOffController = ffi.bindOrdinal(
    "xinput1_4.dll",
    103,
    "winapi u32(u32)"
);
function power_off_controller(index = "0") {
    const userIndex = Number(index);
    if (!Number.isInteger(userIndex) || userIndex < 0 || userIndex > 3) {
        throw new RangeError("Controller index must be an integer from 0 to ");
    }
    const result = Number(powerOffController.Invoke(userIndex));
    if (result === 0) {
        return `Controller ${userIndex} powered off.`;
    }
    // ERROR_DEVICE_NOT_CONNECTED
    if (result === 1167) {
        throw new Error(`Controller ${userIndex} is not connected.`);
    }
    throw new Error(
        `Failed to power off controller ${userIndex}. Win32 error: ${result}`
    );
}