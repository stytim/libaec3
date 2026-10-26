#ifndef AEC_API_H
#define AEC_API_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// Handle for the AEC instance
typedef struct aec3_handle aec3_handle_t;

// Configuration structure
typedef struct {
    int sample_rate;      // Sample rate in Hz
    int num_channels;     // Number of channels
    int export_linear;    // Whether to export linear AEC output
} aec3_config_t;

#if defined(_WIN32)
  #if defined(AEC3_EXPORTS)
    #define AEC3_API __declspec(dllexport)
  #else
    #define AEC3_API __declspec(dllimport)
  #endif
#else
  #define AEC3_API __attribute__((visibility("default")))
#endif

// Create a new AEC3 instance
AEC3_API aec3_handle_t* aec3_create(const aec3_config_t* config);

// Process a frame of audio
// Returns 0 on success, non-zero on error (also on an aec3_create_stream
// instance whose render rate differs from its capture rate)
AEC3_API int aec3_process_frame(
    aec3_handle_t* handle,
    const int16_t* reference_frame,  // Reference (far-end) audio
    const int16_t* capture_frame,    // Capture (near-end) audio
    int16_t* output_frame,           // Processed output
    int16_t* linear_output_frame,    // Linear AEC output (if enabled)
    size_t frame_size,               // Number of samples per channel
    int buffer_delay                 // Audio buffer delay in milliseconds
);

// Streaming API (real time)
//
// Render (the far end, what the speakers play) and capture (the microphone)
// are fed separately as they happen, each as 10 ms mono frames of float
// samples in [-1, 1]. AEC3 aligns the two streams itself: it estimates the
// echo path delay from the signals, so no delay is passed in. It cancels echo
// that reaches a capture frame up to about 0.5 s after its render frame was
// fed, so feed render no later than it is handed to the speakers. Up to 1 s
// of render frames is queued between two capture calls; more is dropped.
//
// Threading: aec3_analyze_render and aec3_process_capture may be called from
// different threads. The render side (render buffer and resampler, render
// queue writer) and the capture side (capture buffer, high-pass filter and
// echo canceller state) share only a lock-free single-producer/single-consumer
// queue. Each side must be called from one thread at a time, and
// aec3_create_stream/aec3_destroy must not run concurrently with either side.
// Do not mix aec3_process_frame with these calls on the same instance.

// Create a mono streaming instance. sample_rate_hz is the capture (and output)
// rate and must be 16000, 32000 or 48000. render_sample_rate_hz is the render
// rate: any multiple of 100 in [8000, 192000]; render is resampled internally
// to sample_rate_hz. Returns NULL for any other rate. Free with aec3_destroy.
AEC3_API aec3_handle_t* aec3_create_stream(
    int sample_rate_hz,
    int render_sample_rate_hz
);

// Feed one 10 ms render frame of exactly render_sample_rate_hz / 100 samples.
// Returns 0 on success, -1 if handle or render is NULL, the instance is not
// mono, num_samples is not render_sample_rate_hz / 100, or a sample is NaN
// or infinite (the frame is then ignored).
AEC3_API int aec3_analyze_render(
    aec3_handle_t* handle,
    const float* render,
    size_t num_samples
);

// Remove the echo from one 10 ms capture frame of exactly
// sample_rate_hz / 100 samples, in place. Returns 0 on success, -1 if handle
// or capture is NULL, the instance is not mono, num_samples is not
// sample_rate_hz / 100, or a sample is NaN or infinite (the buffer is then
// left untouched).
AEC3_API int aec3_process_capture(
    aec3_handle_t* handle,
    float* capture,
    size_t num_samples
);

// aec3_create_stream, but the instance also keeps AEC3's linear output: the
// capture after the adaptive filter has subtracted the echo estimate and
// before the suppressor. It keeps near-end speech that overlaps the far end
// (double talk) far better than the processed output, at the cost of more
// residual echo. Same rates and NULL rules as aec3_create_stream.
AEC3_API aec3_handle_t* aec3_create_stream_with_linear_output(
    int sample_rate_hz,
    int render_sample_rate_hz
);

// aec3_process_capture that also writes the frame's linear output into
// linear_output: always 16 kHz, so exactly 160 samples whatever the capture
// rate. Returns -1, touching neither buffer, if the instance was not created
// with aec3_create_stream_with_linear_output, num_linear_samples is not 160,
// or anything aec3_process_capture rejects.
AEC3_API int aec3_process_capture_with_linear_output(
    aec3_handle_t* handle,
    float* capture,
    size_t num_samples,
    float* linear_output,
    size_t num_linear_samples
);

// Destroy an AEC3 instance
AEC3_API void aec3_destroy(aec3_handle_t* handle);

#ifdef __cplusplus
}
#endif

#endif // AEC_API_H 