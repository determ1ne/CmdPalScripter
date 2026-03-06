const cmdOut = $('echo hello from cmd', { shell: 'cmd' });
const psOut = $('"hello from powershell"', { shell: 'powershell' });

`cmd: ${cmdOut}\npowershell: ${psOut}`;
