function ping(host = '8.8.8.8') {
    const target = String(host || '8.8.8.8').trim();

    if (!/^[A-Za-z0-9._:-]+$/.test(target)) {
        throw new Error('Invalid ping target. Use a host name, IPv4 address, or IPv6 address without spaces.');
    }

    $.exec('cmd.exe', ['/k', 'ping', '-t', target], { window: true });
    return `Started infinite ping for ${target}. Close the Command Prompt window to stop it.`;
}
