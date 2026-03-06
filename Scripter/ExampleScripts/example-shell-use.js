const pwsh = $.use('pwsh');
const output = pwsh('$PSVersionTable.PSVersion.ToString()');

`pwsh version: ${output}`;
