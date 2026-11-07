#include "send_bus_registry.h"

#include <godot_cpp/variant/char_string.hpp>

#include <algorithm>
#include <mutex>
#include <string>
#include <unordered_map>

namespace dirge {

void SendBus::add(uint32_t channel, const godot::AudioFrame *src, uint32_t count, float gain) {
	if (channel >= MAX_CHANNELS) {
		return;
	}
	SendChannel &ch = channels[channel];
	const uint32_t n = std::min(count, SendChannel::CAPACITY);
	godot::AudioFrame *dst = ch.frames;
	for (uint32_t i = 0; i < n; i++) {
		dst[i].left += src[i].left * gain;
		dst[i].right += src[i].right * gain;
	}
	ch.pending = std::max(ch.pending, n);
}

uint32_t SendBus::consume(uint32_t channel, godot::AudioFrame *dst, uint32_t count) {
	if (channel >= MAX_CHANNELS) {
		return 0;
	}
	SendChannel &ch = channels[channel];
	const uint32_t n = std::min(count, ch.pending);
	godot::AudioFrame *src = ch.frames;
	for (uint32_t i = 0; i < n; i++) {
		dst[i].left += src[i].left;
		dst[i].right += src[i].right;
	}
	// Clear everything that was accumulated, including frames beyond `count`
	// (buffer-size mismatch): stale frames must never leak into a later step.
	for (uint32_t i = 0; i < ch.pending; i++) {
		src[i].left = 0.0f;
		src[i].right = 0.0f;
	}
	ch.pending = 0;
	return n;
}

namespace {

struct Registry {
	std::mutex mutex;
	std::unordered_map<std::string, SendBus *> buses;
};

Registry &registry() {
	// Leaked on purpose: audio-thread code may still hold pointers at exit.
	static Registry *instance = new Registry();
	return *instance;
}

} // namespace

SendBus *SendBusRegistry::acquire(const godot::String &name) {
	const godot::CharString utf8 = name.utf8();
	std::string key(utf8.get_data(), static_cast<size_t>(utf8.length()));
	Registry &reg = registry();
	std::lock_guard<std::mutex> guard(reg.mutex);
	auto it = reg.buses.find(key);
	if (it != reg.buses.end()) {
		return it->second;
	}
	SendBus *bus = new SendBus(); // value-initialized: all frames zero
	reg.buses.emplace(std::move(key), bus);
	return bus;
}

} // namespace dirge
