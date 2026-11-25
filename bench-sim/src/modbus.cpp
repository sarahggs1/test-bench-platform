#include "bench/modbus.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>

#include <stdexcept>

namespace bench::modbus {

namespace {

uint16_t be16(const std::vector<uint8_t>& b, std::size_t i) { return static_cast<uint16_t>((b[i] << 8) | b[i + 1]); }

void put16(std::vector<uint8_t>& b, uint16_t v) {
    b.push_back(static_cast<uint8_t>(v >> 8));
    b.push_back(static_cast<uint8_t>(v & 0xFF));
}

std::vector<uint8_t> response(const std::vector<uint8_t>& req, const std::vector<uint8_t>& pdu) {
    std::vector<uint8_t> out(req.begin(), req.begin() + 4);  // transaction id + protocol id
    put16(out, static_cast<uint16_t>(pdu.size() + 1));       // length = unit id + PDU
    out.push_back(req[6]);                                   // unit id
    out.insert(out.end(), pdu.begin(), pdu.end());
    return out;
}

std::vector<uint8_t> exception(const std::vector<uint8_t>& req, uint8_t function, uint8_t code) {
    return response(req, {static_cast<uint8_t>(function | 0x80), code});
}

bool writable(uint16_t address) { return address <= reg::kCommand; }

}  // namespace

std::vector<uint8_t> RegisterMap::handle(const std::vector<uint8_t>& req) {
    if (req.size() < 8 || be16(req, 2) != 0) return {};  // not Modbus TCP: drop
    const uint8_t function = req[7];

    if (function == 0x03) {
        if (req.size() < 12) return exception(req, function, kIllegalValue);
        const uint16_t start = be16(req, 8), count = be16(req, 10);
        if (count == 0 || count > 125) return exception(req, function, kIllegalValue);
        if (start + count > regs_.size()) return exception(req, function, kIllegalAddress);
        std::vector<uint8_t> pdu{function, static_cast<uint8_t>(2 * count)};
        for (uint16_t i = 0; i < count; ++i) put16(pdu, regs_[start + i]);
        return response(req, pdu);
    }

    if (function == 0x06) {
        if (req.size() < 12) return exception(req, function, kIllegalValue);
        const uint16_t address = be16(req, 8), value = be16(req, 10);
        if (address >= regs_.size() || !writable(address)) return exception(req, function, kIllegalAddress);
        if (on_write_ && !on_write_(address, value)) return exception(req, function, kIllegalValue);
        regs_[address] = value;
        return response(req, std::vector<uint8_t>(req.begin() + 7, req.begin() + 12));  // echo
    }

    return exception(req, function, kIllegalFunction);
}

TcpServer::TcpServer(RegisterMap& map, uint16_t port) : map_(map), port_(port) {
    listen_fd_ = ::socket(AF_INET, SOCK_STREAM, 0);
    if (listen_fd_ < 0) throw std::runtime_error("socket() failed");
    int yes = 1;
    ::setsockopt(listen_fd_, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(port);
    if (::bind(listen_fd_, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) < 0 || ::listen(listen_fd_, 4) < 0) {
        ::close(listen_fd_);
        throw std::runtime_error("cannot listen on Modbus port " + std::to_string(port));
    }
    ::fcntl(listen_fd_, F_SETFL, O_NONBLOCK);
}

TcpServer::~TcpServer() {
    for (int fd : clients_) ::close(fd);
    if (listen_fd_ >= 0) ::close(listen_fd_);
}

void TcpServer::poll(int timeout_ms) {
    fd_set fds;
    FD_ZERO(&fds);
    FD_SET(listen_fd_, &fds);
    int max_fd = listen_fd_;
    for (int fd : clients_) {
        FD_SET(fd, &fds);
        max_fd = std::max(max_fd, fd);
    }
    timeval tv{timeout_ms / 1000, (timeout_ms % 1000) * 1000};
    if (::select(max_fd + 1, &fds, nullptr, nullptr, &tv) <= 0) return;

    if (FD_ISSET(listen_fd_, &fds)) {
        int fd = ::accept(listen_fd_, nullptr, nullptr);
        if (fd >= 0) {
            clients_.push_back(fd);
            buffers_.emplace_back();
        }
    }

    for (std::size_t i = 0; i < clients_.size();) {
        int fd = clients_[i];
        if (!FD_ISSET(fd, &fds)) {
            ++i;
            continue;
        }
        uint8_t chunk[260];
        ssize_t n = ::recv(fd, chunk, sizeof(chunk), 0);
        if (n <= 0) {  // client closed the connection
            ::close(fd);
            clients_.erase(clients_.begin() + static_cast<long>(i));
            buffers_.erase(buffers_.begin() + static_cast<long>(i));
            continue;
        }
        auto& buf = buffers_[i];
        buf.insert(buf.end(), chunk, chunk + n);
        // A TCP stream may carry several requests or a partial one: frame them with the MBAP length.
        while (buf.size() >= 6) {
            const std::size_t total = 6 + ((buf[4] << 8) | buf[5]);
            if (buf.size() < total) break;
            std::vector<uint8_t> req(buf.begin(), buf.begin() + static_cast<long>(total));
            buf.erase(buf.begin(), buf.begin() + static_cast<long>(total));
            auto resp = map_.handle(req);
            if (!resp.empty()) ::send(fd, resp.data(), resp.size(), MSG_NOSIGNAL);
        }
        ++i;
    }
}

}  // namespace bench::modbus
