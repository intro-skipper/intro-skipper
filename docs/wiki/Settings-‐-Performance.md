# Performance Settings

These settings control how the plugin utilizes system resources during analysis and caching.

## FFmpeg Process Settings

- **Maximum Degree of Parallelism** (Default: 2)
  - Maximum number of episodes that can be analyzed simultaneously.
  - Higher values speed up analysis but use more CPU and memory.
  - Recommended: 2–4 for most systems.

- **FFmpeg Process Priority** (Default: Below Normal)
  - Sets the CPU scheduling priority of FFmpeg analysis processes relative to other work.
  - Options: Idle, Below Normal, Normal, Above Normal, High, Highest
  - Lower priority reduces the impact on server responsiveness during analysis.

- **FFmpeg Process Threads** (Default: 0 / Auto)
  - Number of CPU threads each FFmpeg process may use.
  - 0 (default) lets FFmpeg determine the optimal thread count automatically.
  - Set a specific value to cap CPU usage on lower-powered systems.

## Audio and Caching

- **Probe Audio Duration for Credits** (Default: Disabled)
  - Uses ffprobe to read the actual audio stream duration before credits fingerprinting.
  - Helps when the container's reported runtime is inflated by subtitle or secondary video tracks, which can cause credits to be missed.

- **Cache Compression Level** (Default: Optimal)
  - Brotli compression level applied to stored audio fingerprint data.
  - Options: No Compression, Fastest, Optimal, Smallest Size
  - Higher compression reduces disk usage at the cost of more CPU time during writes.
  - Changing this setting only affects newly written cache entries.
