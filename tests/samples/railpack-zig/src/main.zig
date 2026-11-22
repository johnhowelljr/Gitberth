const std = @import("std");

// Answers every request on PORT, the web container port Gitberth gives it.
pub fn main() !void {
    const port = if (std.posix.getenv("PORT")) |p| try std.fmt.parseInt(u16, p, 10) else 8080;
    const address = try std.net.Address.parseIp4("0.0.0.0", port);
    var server = try address.listen(.{ .reuse_address = true });
    defer server.deinit();
    const answer = "HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 14\r\nConnection: close\r\n\r\nHello from Zig";
    while (true) {
        const connection = try server.accept();
        defer connection.stream.close();
        var request: [4096]u8 = undefined;
        _ = connection.stream.read(&request) catch continue;
        _ = connection.stream.writeAll(answer) catch {};
    }
}
