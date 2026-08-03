# Codex App Server protocol compatibility

The application does not ship a hand-written historic App Server contract. `CodexRuntimeManager` locates the selected Codex CLI, records its version, invokes the CLI Schema generator, hashes the generated bundle, and validates the minimum methods needed for initialize, model discovery, Thread lifecycle, Turn streaming, interruption, final read, and deletion.

Unknown JSON fields are retained by the transport boundary. A missing required method or invalid Schema blocks native worker creation and produces a diagnostic state; it does not fall back to guessing an older protocol. Tests exercise generated Schema, initialization, model listing, single-worker lifecycle, final read/delete ordering, and three independent workers against the explicitly configured current Codex executable.
