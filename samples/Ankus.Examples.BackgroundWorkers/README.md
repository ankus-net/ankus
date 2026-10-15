# Background worker example

This ports pgrx's background-worker example to a synchronous managed entry.
The worker connects to `postgres`, reads database metadata in separate
transactions and waits on PostgreSQL's latch between observations. Shared
atomic values make its PID and results visible to ordinary SQL sessions.

Publish and install the extension, then add the library to `postgresql.conf`
and restart PostgreSQL:

```ini
shared_preload_libraries = 'Ankus.Examples.BackgroundWorkers'
```

After the worker's first observation:

```sql
CREATE EXTENSION ankus_background_workers;
SELECT observer_process(), observed_databases(), completed_observations();
```

The process ID differs from the querying backend. The database count comes
from the worker's committed query, and the observation count increases every
ten seconds. Reload wakes the worker and reloads its configuration; server
shutdown requests graceful termination. Failure restarts it after five seconds.

The example connects with PostgreSQL's default bootstrap superuser. Supply an
explicit role to `PgBackgroundWorker.Connect` when adapting it for narrower
database privileges. See the [worker guide](https://willibrandon.github.io/ankus/background-workers/)
for dynamic registration and ownership rules.
