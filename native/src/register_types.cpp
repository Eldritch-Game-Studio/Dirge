#include "register_types.h"

#include "audio_effect_convolution_reverb.h"
#include "audio_effect_return.h"
#include "audio_effect_send.h"

#include <gdextension_interface.h>
#include <godot_cpp/core/defs.hpp>
#include <godot_cpp/godot.hpp>

using namespace godot;

void initialize_dirge_native(ModuleInitializationLevel p_level) {
	if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) {
		return;
	}
	GDREGISTER_CLASS(dirge::DirgeAudioEffectSend);
	GDREGISTER_CLASS(dirge::DirgeAudioEffectSendInstance);
	GDREGISTER_CLASS(dirge::DirgeAudioEffectReturn);
	GDREGISTER_CLASS(dirge::DirgeAudioEffectReturnInstance);
	GDREGISTER_CLASS(dirge::DirgeAudioEffectConvolutionReverb);
	GDREGISTER_CLASS(dirge::DirgeAudioEffectConvolutionReverbInstance);
}

void uninitialize_dirge_native(ModuleInitializationLevel p_level) {
	if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) {
		return;
	}
}

extern "C" {
GDExtensionBool GDE_EXPORT dirge_native_init(GDExtensionInterfaceGetProcAddress p_get_proc_address, GDExtensionClassLibraryPtr p_library, GDExtensionInitialization *r_initialization) {
	GDExtensionBinding::InitObject init_obj(p_get_proc_address, p_library, r_initialization);
	init_obj.register_initializer(initialize_dirge_native);
	init_obj.register_terminator(uninitialize_dirge_native);
	init_obj.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);
	return init_obj.init();
}
}
