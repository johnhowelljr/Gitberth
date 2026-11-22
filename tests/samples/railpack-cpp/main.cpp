// Answers every request on PORT, the web container port Gitberth gives it: the smallest HTTP server POSIX sockets make.
#include <arpa/inet.h>
#include <cstdlib>
#include <cstring>
#include <string>
#include <sys/socket.h>
#include <unistd.h>

int main() {
    const char* env = std::getenv("PORT");
    const int port = env ? std::atoi(env) : 8080;
    const int server = socket(AF_INET, SOCK_STREAM, 0);
    const int on = 1;
    setsockopt(server, SOL_SOCKET, SO_REUSEADDR, &on, sizeof on);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons(static_cast<uint16_t>(port));
    if (bind(server, reinterpret_cast<sockaddr*>(&address), sizeof address) != 0 || listen(server, 16) != 0) return 1;
    const std::string body = "Hello from C++";
    const std::string answer = "HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + std::to_string(body.size())
        + "\r\nConnection: close\r\n\r\n" + body;
    for (;;) {
        const int client = accept(server, nullptr, nullptr);
        if (client < 0) continue;
        char request[4096];
        if (read(client, request, sizeof request) > 0) write(client, answer.data(), answer.size());
        close(client);
    }
}
