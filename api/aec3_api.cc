#include "aec3_api.h"
#include "echo_canceller3_factory.h"
#include "echo_canceller3_config.h"
#include "../audio_processing/include/audio_processing.h"
#include "../audio_processing/audio_buffer.h"
#include "../audio_processing/high_pass_filter.h"
#include "../audio_processing/aec3/aec3_common.h"

#include <cmath>

using namespace webrtc;

// AEC3 produces its linear output at 16 kHz regardless of the capture rate.
constexpr int kLinearOutputRateHz = 16000;
constexpr size_t kLinearOutputFrameSamples = kLinearOutputRateHz / 100;

struct aec3_handle {
    std::unique_ptr<EchoControl> echo_controller;
    std::unique_ptr<HighPassFilter> hp_filter;
    std::unique_ptr<AudioBuffer> ref_audio;
    std::unique_ptr<AudioBuffer> aec_audio;
    std::unique_ptr<AudioBuffer> aec_linear_audio;
    AudioFrame ref_frame;
    AudioFrame aec_frame;
    int sample_rate;
    int render_sample_rate;
    int num_channels;
};

aec3_handle_t* aec3_create(const aec3_config_t* config) {
    if (!config) return nullptr;

    auto handle = new aec3_handle_t;
    handle->sample_rate = config->sample_rate;
    handle->render_sample_rate = config->sample_rate;
    handle->num_channels = config->num_channels;

    EchoCanceller3Config aec_config;
    aec_config.filter.export_linear_aec_output = config->export_linear;
    EchoCanceller3Factory aec_factory(aec_config);
    
    handle->echo_controller = aec_factory.Create(
        config->sample_rate, config->num_channels, config->num_channels);
    handle->hp_filter = std::make_unique<HighPassFilter>(
        config->sample_rate, config->num_channels);

    StreamConfig stream_config(config->sample_rate, config->num_channels, false);
    handle->ref_audio = std::make_unique<AudioBuffer>(
        stream_config.sample_rate_hz(), stream_config.num_channels(),
        stream_config.sample_rate_hz(), stream_config.num_channels(),
        stream_config.sample_rate_hz(), stream_config.num_channels());
    
    handle->aec_audio = std::make_unique<AudioBuffer>(
        stream_config.sample_rate_hz(), stream_config.num_channels(),
        stream_config.sample_rate_hz(), stream_config.num_channels(),
        stream_config.sample_rate_hz(), stream_config.num_channels());

    if (config->export_linear) {
        handle->aec_linear_audio = std::make_unique<AudioBuffer>(
            kLinearOutputRateHz, stream_config.num_channels(),
            kLinearOutputRateHz, stream_config.num_channels(),
            kLinearOutputRateHz, stream_config.num_channels());
    }

    return handle;
}

int aec3_process_frame(
    aec3_handle_t* handle,
    const int16_t* reference_frame,
    const int16_t* capture_frame,
    int16_t* output_frame,
    int16_t* linear_output_frame,
    size_t frame_size,
    int buffer_delay) {
    
    if (!handle || !reference_frame || !capture_frame || !output_frame ||
        handle->render_sample_rate != handle->sample_rate) {
        return -1;
    }

    // Update frames with new audio data
    handle->ref_frame.UpdateFrame(0, const_cast<int16_t*>(reference_frame),
        frame_size, handle->sample_rate,
        AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);
    
    handle->aec_frame.UpdateFrame(0, const_cast<int16_t*>(capture_frame),
        frame_size, handle->sample_rate,
        AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);

    // Copy frames to audio buffers
    handle->ref_audio->CopyFrom(&handle->ref_frame);
    handle->aec_audio->CopyFrom(&handle->aec_frame);

    // Process audio
    handle->ref_audio->SplitIntoFrequencyBands();
    handle->echo_controller->AnalyzeRender(handle->ref_audio.get());
    handle->ref_audio->MergeFrequencyBands();
    
    handle->echo_controller->AnalyzeCapture(handle->aec_audio.get());
    handle->aec_audio->SplitIntoFrequencyBands();
    handle->hp_filter->Process(handle->aec_audio.get(), true);
    
    // Use the buffer_delay parameter instead of hardcoded value
    handle->echo_controller->SetAudioBufferDelay(buffer_delay);
    
    if (handle->aec_linear_audio) {
        handle->echo_controller->ProcessCapture(
            handle->aec_audio.get(), handle->aec_linear_audio.get(), false);
    } else {
        handle->echo_controller->ProcessCapture(
            handle->aec_audio.get(), nullptr, false);
    }
    
    handle->aec_audio->MergeFrequencyBands();

    // Copy processed audio back to output
    handle->aec_audio->CopyTo(&handle->aec_frame);
    memcpy(output_frame, handle->aec_frame.data(),
        frame_size * handle->num_channels * sizeof(int16_t));

    // Copy linear output if requested
    if (linear_output_frame && handle->aec_linear_audio) {
        handle->aec_frame.UpdateFrame(0, nullptr,
            kLinearOutputRateHz / 100, kLinearOutputRateHz,
            AudioFrame::kNormalSpeech, AudioFrame::kVadActive, 1);
        handle->aec_linear_audio->CopyTo(&handle->aec_frame);
        memcpy(linear_output_frame, handle->aec_frame.data(), 320);
    }

    return 0;
}

// A single NaN (or an infinity, which the render resampler turns into NaN)
// permanently poisons the echo canceller state.
static bool AllFinite(const float* samples, size_t num_samples) {
    for (size_t i = 0; i < num_samples; ++i) {
        if (!std::isfinite(samples[i])) {
            return false;
        }
    }
    return true;
}

static aec3_handle_t* CreateStream(
    int sample_rate_hz,
    int render_sample_rate_hz,
    int export_linear) {
    if (!ValidFullBandRate(sample_rate_hz) ||
        render_sample_rate_hz < 8000 || render_sample_rate_hz > 192000 ||
        render_sample_rate_hz % 100 != 0) {
        return nullptr;
    }

    aec3_config_t config = {sample_rate_hz, 1, export_linear};
    aec3_handle_t* handle = aec3_create(&config);
    handle->render_sample_rate = render_sample_rate_hz;
    handle->ref_audio = std::make_unique<AudioBuffer>(
        render_sample_rate_hz, 1, sample_rate_hz, 1, sample_rate_hz, 1);
    return handle;
}

aec3_handle_t* aec3_create_stream(
    int sample_rate_hz,
    int render_sample_rate_hz) {
    return CreateStream(sample_rate_hz, render_sample_rate_hz, 0);
}

aec3_handle_t* aec3_create_stream_with_linear_output(
    int sample_rate_hz,
    int render_sample_rate_hz) {
    return CreateStream(sample_rate_hz, render_sample_rate_hz, 1);
}

int aec3_analyze_render(
    aec3_handle_t* handle,
    const float* render,
    size_t num_samples) {
    if (!handle || !render || handle->num_channels != 1 ||
        num_samples != static_cast<size_t>(handle->render_sample_rate / 100) ||
        !AllFinite(render, num_samples)) {
        return -1;
    }

    handle->ref_audio->CopyFrom(
        &render, StreamConfig(handle->render_sample_rate, 1));
    handle->ref_audio->SplitIntoFrequencyBands();
    handle->echo_controller->AnalyzeRender(handle->ref_audio.get());
    return 0;
}

static bool ValidCaptureFrame(
    const aec3_handle_t* handle,
    const float* capture,
    size_t num_samples) {
    return handle && capture && handle->num_channels == 1 &&
        num_samples == static_cast<size_t>(handle->sample_rate / 100) &&
        AllFinite(capture, num_samples);
}

// linear_output may only be non-NULL on an instance that exports it.
static void ProcessCaptureFrame(
    aec3_handle_t* handle,
    float* capture,
    float* linear_output) {
    const StreamConfig stream_config(handle->sample_rate, 1);
    handle->aec_audio->CopyFrom(&capture, stream_config);
    handle->echo_controller->AnalyzeCapture(handle->aec_audio.get());
    handle->aec_audio->SplitIntoFrequencyBands();
    handle->hp_filter->Process(handle->aec_audio.get(), true);
    handle->echo_controller->ProcessCapture(
        handle->aec_audio.get(), handle->aec_linear_audio.get(), false);
    handle->aec_audio->MergeFrequencyBands();
    handle->aec_audio->CopyTo(stream_config, &capture);
    if (linear_output) {
        handle->aec_linear_audio->CopyTo(
            StreamConfig(kLinearOutputRateHz, 1), &linear_output);
    }
}

int aec3_process_capture(
    aec3_handle_t* handle,
    float* capture,
    size_t num_samples) {
    if (!ValidCaptureFrame(handle, capture, num_samples)) {
        return -1;
    }

    ProcessCaptureFrame(handle, capture, nullptr);
    return 0;
}

int aec3_process_capture_with_linear_output(
    aec3_handle_t* handle,
    float* capture,
    size_t num_samples,
    float* linear_output,
    size_t num_linear_samples) {
    if (!ValidCaptureFrame(handle, capture, num_samples) || !linear_output ||
        !handle->aec_linear_audio ||
        num_linear_samples != kLinearOutputFrameSamples) {
        return -1;
    }

    ProcessCaptureFrame(handle, capture, linear_output);
    return 0;
}

void aec3_destroy(aec3_handle_t* handle) {
    delete handle;
} 