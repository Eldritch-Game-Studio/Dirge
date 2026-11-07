#include "bus_locator.h"

#include <godot_cpp/classes/audio_server.hpp>

namespace dirge {

using godot::AudioEffect;
using godot::AudioEffectInstance;
using godot::AudioServer;
using godot::Ref;

namespace {

bool instance_at(AudioServer *server, const BusLocation &loc, const AudioEffect *effect, const AudioEffectInstance *instance) {
	if (loc.bus < 0 || loc.bus >= server->get_bus_count()) {
		return false;
	}
	if (loc.effect < 0 || loc.effect >= server->get_bus_effect_count(loc.bus)) {
		return false;
	}
	if (loc.channel < 0 || loc.channel >= server->get_bus_channels(loc.bus)) {
		return false;
	}
	const Ref<AudioEffect> at = server->get_bus_effect(loc.bus, loc.effect);
	if (at.ptr() != effect) {
		return false;
	}
	const Ref<AudioEffectInstance> inst = server->get_bus_effect_instance(loc.bus, loc.effect, loc.channel);
	return inst.ptr() == instance;
}

} // namespace

bool locate_instance(const AudioEffect *effect, const AudioEffectInstance *instance, BusLocation &loc) {
	AudioServer *server = AudioServer::get_singleton();
	if (server == nullptr) {
		loc = BusLocation();
		return false;
	}
	if (instance_at(server, loc, effect, instance)) {
		return true;
	}
	const int32_t bus_count = server->get_bus_count();
	for (int32_t b = 0; b < bus_count; b++) {
		const int32_t effect_count = server->get_bus_effect_count(b);
		for (int32_t e = 0; e < effect_count; e++) {
			const Ref<AudioEffect> at = server->get_bus_effect(b, e);
			if (at.ptr() != effect) {
				continue;
			}
			const int32_t channels = server->get_bus_channels(b);
			for (int32_t c = 0; c < channels; c++) {
				const Ref<AudioEffectInstance> inst = server->get_bus_effect_instance(b, e, c);
				if (inst.ptr() == instance) {
					loc.bus = b;
					loc.effect = e;
					loc.channel = c;
					return true;
				}
			}
		}
	}
	loc = BusLocation();
	return false;
}

int32_t find_effect_bus(const AudioEffect *effect) {
	AudioServer *server = AudioServer::get_singleton();
	if (server == nullptr) {
		return -1;
	}
	const int32_t bus_count = server->get_bus_count();
	for (int32_t b = 0; b < bus_count; b++) {
		const int32_t effect_count = server->get_bus_effect_count(b);
		for (int32_t e = 0; e < effect_count; e++) {
			const Ref<AudioEffect> at = server->get_bus_effect(b, e);
			if (at.ptr() == effect) {
				return b;
			}
		}
	}
	return -1;
}

} // namespace dirge
