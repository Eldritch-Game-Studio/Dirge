#include "audio_effect_return.h"

#include <godot_cpp/classes/audio_server.hpp>
#include <godot_cpp/classes/audio_stream_generator.hpp>
#include <godot_cpp/classes/audio_stream_player.hpp>
#include <godot_cpp/classes/engine.hpp>
#include <godot_cpp/classes/scene_tree.hpp>
#include <godot_cpp/classes/window.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/core/memory.hpp>
#include <godot_cpp/variant/node_path.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <cstring>

namespace dirge {

using godot::AudioEffectInstance;
using godot::AudioFrame;
using godot::AudioServer;
using godot::AudioStreamGenerator;
using godot::AudioStreamPlayer;
using godot::ClassDB;
using godot::D_METHOD;
using godot::Engine;
using godot::Node;
using godot::NodePath;
using godot::Ref;
using godot::SceneTree;
using godot::String;
using godot::StringName;
using godot::Window;

namespace {

const char *KEEP_ALIVE_PREFIX = "DirgeReturnKeepAlive_";

} // namespace

void DirgeAudioEffectReturn::_bind_methods() {
	ClassDB::bind_method(D_METHOD("_ensure_keep_alive"), &DirgeAudioEffectReturn::_ensure_keep_alive);
}

Ref<AudioEffectInstance> DirgeAudioEffectReturn::_instantiate() {
	Ref<DirgeAudioEffectReturnInstance> instance;
	instance.instantiate();
	instance->base = Ref<DirgeAudioEffectReturn>(this);
	// add_bus_effect() calls _instantiate() before inserting the effect into the
	// bus, so the bus can only be resolved on the next main-loop iteration.
	request_keep_alive();
	return instance;
}

void DirgeAudioEffectReturn::request_keep_alive() {
	bool expected = false;
	if (keep_alive_pending.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
		call_deferred(StringName("_ensure_keep_alive"));
	}
}

namespace {

// True when `bus` exists and its chain contains a DirgeAudioEffectReturn.
bool bus_has_return(AudioServer *server, const String &bus) {
	const int32_t index = server->get_bus_index(StringName(bus));
	if (index < 0) {
		return false;
	}
	const int32_t effect_count = server->get_bus_effect_count(index);
	for (int32_t e = 0; e < effect_count; e++) {
		const Ref<godot::AudioEffect> effect = server->get_bus_effect(index, e);
		if (godot::Object::cast_to<DirgeAudioEffectReturn>(effect.ptr()) != nullptr) {
			return true;
		}
	}
	return false;
}

// Frees keep-alive players whose bus vanished or lost its return effect.
void prune_keep_alives(AudioServer *server, Window *root) {
	const godot::TypedArray<Node> children = root->get_children();
	for (int64_t i = 0; i < children.size(); i++) {
		godot::Object *child = children[i];
		AudioStreamPlayer *player = godot::Object::cast_to<AudioStreamPlayer>(child);
		if (player == nullptr || !String(player->get_name()).begins_with(KEEP_ALIVE_PREFIX)) {
			continue;
		}
		if (!bus_has_return(server, String(player->get_bus()))) {
			player->queue_free();
		}
	}
}

} // namespace

void DirgeAudioEffectReturn::_ensure_keep_alive() {
	keep_alive_pending.store(false, std::memory_order_release);

	Engine *engine = Engine::get_singleton();
	SceneTree *tree = engine != nullptr ? godot::Object::cast_to<SceneTree>(engine->get_main_loop()) : nullptr;
	if (tree == nullptr) {
		return; // Headless script without a SceneTree: nothing can keep a bus alive here.
	}
	Window *root = tree->get_root();
	if (root == nullptr) {
		return;
	}
	AudioServer *server = AudioServer::get_singleton();
	prune_keep_alives(server, root);

	const int32_t bus = find_effect_bus(this);
	if (bus < 0) {
		return; // Not on a bus (yet); the instance re-requests when it locates itself.
	}
	const String name = server->get_bus_name(bus);
	const String node_name = String(KEEP_ALIVE_PREFIX) + name.validate_node_name();
	AudioStreamPlayer *player = godot::Object::cast_to<AudioStreamPlayer>(root->get_node_or_null(NodePath(node_name)));
	if (player != nullptr) {
		player->set_bus(StringName(name));
		if (!player->is_playing()) {
			player->play();
		}
		return;
	}

	Ref<AudioStreamGenerator> silence;
	silence.instantiate();
	silence->set_buffer_length(0.05f);

	player = memnew(AudioStreamPlayer);
	player->set_name(node_name);
	player->set_process_mode(Node::PROCESS_MODE_ALWAYS);
	player->set_stream(silence);
	player->set_bus(StringName(name));
	root->add_child(player);
	player->play();
}

void DirgeAudioEffectReturnInstance::_process(const void *p_src_buffer, AudioFrame *r_dst_buffer, int32_t p_frame_count) {
	const AudioFrame *src = static_cast<const AudioFrame *>(p_src_buffer);
	const uint32_t count = p_frame_count > 0 ? static_cast<uint32_t>(p_frame_count) : 0;
	std::memcpy(r_dst_buffer, src, count * sizeof(AudioFrame));

	if (!locate_instance(base.ptr(), this, location)) {
		source = nullptr;
		return;
	}

	// A bus rename or move keeps the instance alive but changes the name the
	// senders address; re-resolve the registry entry only when it changed.
	const String current = AudioServer::get_singleton()->get_bus_name(location.bus);
	if (source == nullptr || current != bus_name) {
		bus_name = current;
		source = SendBusRegistry::acquire(bus_name);
		base->request_keep_alive();
	}

	source->consume(static_cast<uint32_t>(location.channel), r_dst_buffer, count);
}

} // namespace dirge
