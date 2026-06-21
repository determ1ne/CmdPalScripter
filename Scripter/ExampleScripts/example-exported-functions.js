function add_many_numbers(...args) {
    return args.reduce((sum, value) => sum + Number(value), 0);
}

function add_just_two(a, b) {
    return Number(a) + Number(b);
}

function print_example(...args) {
    return `Example function called with ${args.length} arguments: ${args.join(', ')}`;
}
