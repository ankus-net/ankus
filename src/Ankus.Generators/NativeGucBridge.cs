namespace Ankus.Generators;

/// <summary>
/// Defines native configuration storage, PostgreSQL registration, and guarded managed hook transports.
/// </summary>
internal static class NativeGucBridge
{
    /// <summary>
    /// Gets descriptors shared by native-only registration and managed configuration callbacks.
    /// </summary>
    internal const string Declarations = """
        #include "utils/guc.h"
        #include "utils/guc_tables.h"
        #include "utils/memutils.h"
        #include "miscadmin.h"
        #include "access/xact.h"
        #include "utils/snapmgr.h"
        #if PG_VERSION_NUM < 180000
        #include "access/parallel.h"
        #endif
        #include <limits.h>
        #include <math.h>

        #ifndef ANKUS_GUC_DEFINE_KIND
        #define ANKUS_GUC_DEFINE_KIND 100
        #endif
        #ifndef ANKUS_GUC_LOCATE_KIND
        #define ANKUS_GUC_LOCATE_KIND 200
        #endif

        /* PostgreSQL 19 embeds typed bodies in the generic record instead of
         * placing a generic prefix in each separately typed record. */
        #if PG_VERSION_NUM >= 190000
        #define ANKUS_GUC_RECORD(setting, kind) (&(setting)->_##kind)
        #else
        #define ANKUS_GUC_RECORD(setting, kind) ((struct config_##kind *) (setting))
        #endif

        struct AnkusError;
        struct AnkusRequest;
        struct AnkusResult;
        struct AnkusMemoryApi;
        typedef int (*AnkusGucRead)(const char *, int, AnkusValue *, struct AnkusError *);
        typedef int (*AnkusGucLog)(int, int, struct AnkusError *, struct AnkusError *, int *);
        typedef int (*AnkusGucHook)(int, AnkusValue *, AnkusValue *, int, struct AnkusError *, AnkusGucRead,
            int (*)(struct AnkusRequest *, struct AnkusResult *, struct AnkusError *), AnkusGucLog, struct AnkusMemoryApi *);

        typedef union AnkusGucNumber
        {
            bool boolean;
            int integer;
            double real;
            const char *string;
        } AnkusGucNumber;

        typedef union AnkusGucCheck
        {
            GucBoolCheckHook boolean;
            GucIntCheckHook integer;
            GucRealCheckHook real;
            GucStringCheckHook string;
            GucEnumCheckHook enumeration;
        } AnkusGucCheck;

        typedef union AnkusGucAssign
        {
            GucBoolAssignHook boolean;
            GucIntAssignHook integer;
            GucRealAssignHook real;
            GucStringAssignHook string;
            GucEnumAssignHook enumeration;
        } AnkusGucAssign;

        typedef struct AnkusGuc
        {
            const char *name_utf8;
            const char *short_utf8;
            const char *long_utf8;
            int kind;
            int context;
            unsigned int flags;
            int unit;
            void *variable;
            AnkusGucNumber boot;
            AnkusGucNumber minimum;
            AnkusGucNumber maximum;
            const struct config_enum_entry *options_utf8;
            AnkusGucCheck check;
            AnkusGucAssign assign;
            GucShowHook show;
            AnkusGucHook hook;
            bool has_check;
            bool has_assign;
            bool has_show;
            bool worker_restore_pending;
            bool prepared;
            bool installed;
            char *name;
            char *short_desc;
            char *long_desc;
            char *boot_string;
            struct config_enum_entry *options;
            void *extra;
            char *show_buffer;
            /* Nonzero for a run-time definition with hooks: the managed hook table entry it dispatches to. */
            int runtime_slot;
        } AnkusGuc;

        """;

    /// <summary>
    /// Gets native registration helpers that never initialize the managed runtime.
    /// </summary>
    internal const string Registration = """
        #if PG_VERSION_NUM < 160000
        static bool
        ankus_guc_name_equal(const char *left, const char *right)
        {
            while (*left != 0 && *right != 0)
            {
                unsigned char a = (unsigned char) *left++;
                unsigned char b = (unsigned char) *right++;
                if (a >= 'A' && a <= 'Z') a += 'a' - 'A';
                if (b >= 'A' && b <= 'Z') b += 'a' - 'A';
                if (a != b) return false;
            }

            return *left == *right;
        }
        #endif

        static void *
        ankus_guc_allocate(Size size)
        {
        #if PG_VERSION_NUM >= 160000
            return guc_malloc(ERROR, size);
        #else
            void *result = malloc(size);
            if (result == NULL)
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("out of memory")));
            return result;
        #endif
        }

        static bool
        ankus_guc_ascii(const char *value)
        {
            if (value != NULL)
            {
                for (const unsigned char *current = (const unsigned char *) value; *current != 0; current++)
                {
                    if (*current >= 128)
                        return false;
                }
            }

            return true;
        }

        static char *
        ankus_guc_persistent_text(const char *utf8)
        {
            if (utf8 == NULL)
                return NULL;
            char *converted = pg_any_to_server(utf8, strlen(utf8), PG_UTF8);
            Size length = strlen(converted) + 1;
            char *copy = ankus_guc_allocate(length);
            memcpy(copy, converted, length);
            if (converted != utf8)
                pfree(converted);
            return copy;
        }

        static GucContext
        ankus_guc_context(int context)
        {
            switch (context)
            {
                case 0: return PGC_INTERNAL;
                case 1: return PGC_POSTMASTER;
                case 2: return PGC_SIGHUP;
                case 3: return PGC_SU_BACKEND;
                case 4: return PGC_BACKEND;
                case 5: return PGC_SUSET;
                case 6: return PGC_USERSET;
                default: elog(ERROR, "invalid Ankus configuration context"); return PGC_INTERNAL;
            }
        }

        static int
        ankus_guc_flags(unsigned int flags, int unit)
        {
            int result = 0;
            if (flags & 1U) result |= GUC_NO_SHOW_ALL | GUC_NOT_IN_SAMPLE;
            if (flags & 2U) result |= GUC_NO_RESET_ALL;
            if (flags & 4U) result |= GUC_REPORT;
            if (flags & 8U) result |= GUC_DISALLOW_IN_FILE;
            if (flags & 16U) result |= GUC_SUPERUSER_ONLY;
            if (flags & 32U) result |= GUC_IS_NAME;
            if (flags & 64U) result |= GUC_NOT_WHILE_SEC_REST;
            if (flags & 128U) result |= GUC_DISALLOW_IN_AUTO_FILE;
            if (flags & 256U) result |= GUC_EXPLAIN;
            if (flags & 512U)
            {
        #if PG_VERSION_NUM >= 150000
                result |= GUC_RUNTIME_COMPUTED;
        #else
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("RuntimeComputed configuration requires PostgreSQL 15 or later")));
        #endif
            }

            switch (unit)
            {
                case 0: break;
                case 1: result |= GUC_UNIT_BYTE; break;
                case 2: result |= GUC_UNIT_KB; break;
                case 3: result |= GUC_UNIT_BLOCKS; break;
                case 4: result |= GUC_UNIT_XBLOCKS; break;
                case 5: result |= GUC_UNIT_MB; break;
                case 6: result |= GUC_UNIT_MS; break;
                case 7: result |= GUC_UNIT_S; break;
                case 8: result |= GUC_UNIT_MIN; break;
                default: elog(ERROR, "invalid Ankus configuration unit");
            }

            return result;
        }

        static struct config_generic *
        ankus_guc_find(const char *name)
        {
        #if PG_VERSION_NUM >= 160000
            return find_option(name, false, true, DEBUG5);
        #else
            struct config_generic **variables = get_guc_variables();
            int count = GetNumConfigOptions();
            for (int index = 0; index < count; index++)
            {
                if (ankus_guc_name_equal(variables[index]->name, name))
                    return variables[index];
            }

            return NULL;
        #endif
        }

        static bool
        ankus_guc_is_owned(AnkusGuc *definition, struct config_generic *existing)
        {
            if (existing == NULL || (existing->flags & GUC_CUSTOM_PLACEHOLDER) != 0)
                return false;
            switch (definition->kind)
            {
                case 0: return existing->vartype == PGC_BOOL && ANKUS_GUC_RECORD(existing, bool)->variable == definition->variable;
                case 1: return existing->vartype == PGC_INT && ANKUS_GUC_RECORD(existing, int)->variable == definition->variable;
                case 2: return existing->vartype == PGC_REAL && ANKUS_GUC_RECORD(existing, real)->variable == definition->variable;
                case 3: return existing->vartype == PGC_STRING && ANKUS_GUC_RECORD(existing, string)->variable == definition->variable;
                case 4: return existing->vartype == PGC_ENUM && ANKUS_GUC_RECORD(existing, enum)->variable == definition->variable;
                default: return false;
            }
        }

        static void
        ankus_guc_prepare(AnkusGuc *definition)
        {
            if (definition->prepared)
                return;
            if (IsPostmasterEnvironment && !IsUnderPostmaster)
            {
                bool ascii = ankus_guc_ascii(definition->name_utf8) && ankus_guc_ascii(definition->short_utf8) &&
                    ankus_guc_ascii(definition->long_utf8) && (definition->kind != 3 || ankus_guc_ascii(definition->boot.string));
                if (definition->kind == 4)
                {
                    for (const struct config_enum_entry *option = definition->options_utf8; option->name != NULL; option++)
                        ascii = ascii && ankus_guc_ascii(option->name);
                }

                if (!ascii)
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("Non-ASCII Ankus configuration metadata cannot be registered through shared_preload_libraries"),
                        errdetail("Database encoding is unavailable in the postmaster; register this definition in a backend.")));
            }

            if (definition->name == NULL)
                definition->name = ankus_guc_persistent_text(definition->name_utf8);
            if (definition->short_desc == NULL)
                definition->short_desc = ankus_guc_persistent_text(definition->short_utf8);
            if (definition->long_desc == NULL)
                definition->long_desc = ankus_guc_persistent_text(definition->long_utf8);
            if (definition->kind == 3)
            {
                if (definition->boot_string == NULL)
                    definition->boot_string = ankus_guc_persistent_text(definition->boot.string);
                *((char **) definition->variable) = definition->boot_string;
            }

            if (definition->kind == 4)
            {
                int count = 0;
                while (definition->options_utf8[count].name != NULL)
                    count++;
                if (definition->options == NULL)
                {
                    definition->options = ankus_guc_allocate(sizeof(struct config_enum_entry) * (Size) (count + 1));
                    memset(definition->options, 0, sizeof(struct config_enum_entry) * (Size) (count + 1));
                }

                for (int index = 0; index < count; index++)
                {
                    if (definition->options[index].name == NULL)
                        definition->options[index].name = ankus_guc_persistent_text(definition->options_utf8[index].name);
                    definition->options[index].val = definition->options_utf8[index].val;
                    definition->options[index].hidden = definition->options_utf8[index].hidden;
                }
            }

            definition->prepared = true;
        }

        /* The definition PostgreSQL is installing; its check runs on the boot value before the setting exists. */
        static AnkusGuc *ankus_guc_defining = NULL;

        static void
        ankus_guc_register(AnkusGuc *definition)
        {
            if (definition->installed)
                return;
            GucContext context = ankus_guc_context(definition->context);
            if (context == PGC_POSTMASTER && !process_shared_preload_libraries_in_progress)
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("Postmaster configuration must be registered through shared_preload_libraries")));
            int flags = ankus_guc_flags(definition->flags, definition->unit);
            ankus_guc_prepare(definition);
            /* A reloaded library finds its own setting already defined and adopts it. */
            struct config_generic *existing = ankus_guc_find(definition->name);
            if (ankus_guc_is_owned(definition, existing))
            {
                definition->installed = true;
                definition->extra = existing->extra;
                return;
            }

            if (existing != NULL && (existing->flags & GUC_CUSTOM_PLACEHOLDER) == 0)
                ereport(ERROR, (errcode(ERRCODE_DUPLICATE_OBJECT),
                    errmsg("configuration parameter \"%s\" is already defined by another owner", definition->name)));
            /* On Windows, a backend-context setting whose placeholder holds a value from the configuration file keeps
             * that value: it is saved here and set again once the setting is defined. */
            char *reload_value = NULL;
            bool reload_pending = false;
            GucContext reload_context = PGC_INTERNAL;
            GucSource reload_source = PGC_S_DEFAULT;
        #if PG_VERSION_NUM >= 150000
            Oid reload_role = InvalidOid;
        #endif
        #ifdef WIN32
            if (existing != NULL && IsUnderPostmaster && existing->scontext == PGC_SIGHUP &&
                (context == PGC_BACKEND || context == PGC_SU_BACKEND))
            {
                struct config_string *placeholder = ANKUS_GUC_RECORD(existing, string);
                reload_pending = true;
                if (*placeholder->variable != NULL)
                    reload_value = pstrdup(*placeholder->variable);
                reload_context = existing->scontext;
                reload_source = existing->source;
        #if PG_VERSION_NUM >= 150000
                reload_role = existing->srole;
        #endif
            }
        #endif
            AnkusGuc *previous_defining = ankus_guc_defining;
            ankus_guc_defining = definition;
            PG_TRY();
            {
                switch (definition->kind)
                {
                    case 0:
                        DefineCustomBoolVariable(definition->name, definition->short_desc, definition->long_desc,
                            definition->variable, definition->boot.boolean, context, flags,
                            definition->check.boolean, definition->assign.boolean, definition->show);
                        break;
                    case 1:
                        DefineCustomIntVariable(definition->name, definition->short_desc, definition->long_desc,
                            definition->variable, definition->boot.integer, definition->minimum.integer, definition->maximum.integer,
                            context, flags, definition->check.integer, definition->assign.integer, definition->show);
                        break;
                    case 2:
                        DefineCustomRealVariable(definition->name, definition->short_desc, definition->long_desc,
                            definition->variable, definition->boot.real, definition->minimum.real, definition->maximum.real,
                            context, flags, definition->check.real, definition->assign.real, definition->show);
                        break;
                    case 3:
                        DefineCustomStringVariable(definition->name, definition->short_desc, definition->long_desc,
                            definition->variable, definition->boot_string, context, flags,
                            definition->check.string, definition->assign.string, definition->show);
                        break;
                    case 4:
                        DefineCustomEnumVariable(definition->name, definition->short_desc, definition->long_desc,
                            definition->variable, definition->boot.integer, definition->options, context, flags,
                            definition->check.enumeration, definition->assign.enumeration, definition->show);
                        break;
                    default: elog(ERROR, "invalid Ankus configuration kind");
                }
            }
            PG_FINALLY();
            {
                ankus_guc_defining = previous_defining;
            }
            PG_END_TRY();

            if (reload_pending)
            {
        #if PG_VERSION_NUM >= 150000
                int result = set_config_option_ext(definition->name, reload_value,
                    reload_context, reload_source, reload_role,
                    GUC_ACTION_SET, true, ERROR, true);
        #else
                int result = set_config_option(definition->name, reload_value,
                    reload_context, reload_source, GUC_ACTION_SET, true, ERROR, true);
        #endif
                if (reload_value != NULL)
                    pfree(reload_value);
                if (result <= 0)
                    ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                        errmsg("Ankus configuration could not apply a reloaded backend value")));
            }

            /* PostgreSQL may have replaced the record while defining it, so its extra state is read again. */
            struct config_generic *registered = ankus_guc_find(definition->name);
            if (!ankus_guc_is_owned(definition, registered))
                ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                    errmsg("Ankus configuration registration lost ownership")));
            definition->extra = registered->extra;
            definition->installed = true;
        }
        """;

    /// <summary>
    /// Gets persistent storage and registration for configuration parameters defined at run time.
    /// </summary>
    /// <remarks>
    /// Emitted after <see cref="Registration"/> and before the read source, which searches these definitions
    /// after the generated ones. Definitions live for the process, like PostgreSQL's own configuration records.
    /// </remarks>
    internal const string RuntimeDefinitions = """
        #define ANKUS_GUC_RUNTIME 1

        static bool ankus_guc_name_equal(const char *, const char *);
        static int ankus_guc_define_runtime(const char *, intptr_t, AnkusError *);
        static void ankus_guc_runtime_bind(AnkusGuc *, int, int);

        /* Run-time definitions with hooks dispatch through one of these prebuilt hook sets. */
        #define ANKUS_GUC_RUNTIME_SLOTS 64
        static AnkusGuc *ankus_guc_runtime_slots[ANKUS_GUC_RUNTIME_SLOTS + 1];

        typedef struct AnkusGucDefinitionRequest
        {
            const char *short_utf8;
            const char *long_utf8;
            const char *boot_string;
            const char *const *option_names;
            const int *option_values;
            const uint8 *option_hidden;
            double boot_real;
            double minimum_real;
            double maximum_real;
            int boot_integer;
            int minimum_integer;
            int maximum_integer;
            int option_count;
            int kind;
            int context;
            int unit;
            unsigned int flags;
            uint8 boot_boolean;
            uint8 boot_null;
            /* Check, assign and show hook bits, and the managed hook table slot that serves them. */
            int hooks;
            int slot;
        } AnkusGucDefinitionRequest;

        typedef struct AnkusRuntimeGuc
        {
            AnkusGuc definition;
            struct AnkusRuntimeGuc *next;
        } AnkusRuntimeGuc;

        static AnkusRuntimeGuc *ankus_guc_runtime = NULL;

        static char *
        ankus_guc_runtime_text(const char *utf8)
        {
            if (utf8 == NULL)
                return NULL;
            Size length = strlen(utf8) + 1;
            char *copy = ankus_guc_allocate(length);
            memcpy(copy, utf8, length);
            return copy;
        }

        static bool
        ankus_guc_runtime_text_equal(const char *left, const char *right)
        {
            return left == NULL ? right == NULL : right != NULL && strcmp(left, right) == 0;
        }

        static AnkusGuc *
        ankus_guc_runtime_find(const char *name)
        {
            for (AnkusRuntimeGuc *current = ankus_guc_runtime; current != NULL; current = current->next)
            {
                if (ankus_guc_name_equal(current->definition.name_utf8, name))
                    return &current->definition;
            }

            return NULL;
        }

        /* A repeated definition, such as one from a retried module load, must describe the same parameter. */
        static bool
        ankus_guc_runtime_same(const AnkusGuc *existing, const AnkusGucDefinitionRequest *input)
        {
            if (existing->kind != input->kind || existing->context != input->context || existing->flags != input->flags ||
                existing->unit != input->unit || !ankus_guc_runtime_text_equal(existing->short_utf8, input->short_utf8) ||
                !ankus_guc_runtime_text_equal(existing->long_utf8, input->long_utf8) ||
                existing->runtime_slot != (input->hooks == 0 ? 0 : input->slot) || existing->has_check != ((input->hooks & 1) != 0) ||
                existing->has_assign != ((input->hooks & 2) != 0) || existing->has_show != ((input->hooks & 4) != 0))
                return false;
            switch (input->kind)
            {
                case 0: return existing->boot.boolean == (input->boot_boolean != 0);
                case 1: return existing->boot.integer == input->boot_integer &&
                    existing->minimum.integer == input->minimum_integer && existing->maximum.integer == input->maximum_integer;
                case 2: return memcmp(&existing->boot.real, &input->boot_real, sizeof(double)) == 0 &&
                    memcmp(&existing->minimum.real, &input->minimum_real, sizeof(double)) == 0 &&
                    memcmp(&existing->maximum.real, &input->maximum_real, sizeof(double)) == 0;
                case 3: return ankus_guc_runtime_text_equal(existing->boot.string, input->boot_null ? NULL : input->boot_string);
                case 4:
                {
                    if (existing->boot.integer != input->boot_integer)
                        return false;
                    int count = 0;
                    while (existing->options_utf8[count].name != NULL)
                        count++;
                    if (count != input->option_count)
                        return false;
                    for (int index = 0; index < count; index++)
                    {
                        if (strcmp(existing->options_utf8[index].name, input->option_names[index]) != 0 ||
                            existing->options_utf8[index].val != input->option_values[index] ||
                            existing->options_utf8[index].hidden != (input->option_hidden[index] != 0))
                            return false;
                    }

                    return true;
                }
                default: return false;
            }
        }

        static void
        ankus_guc_define_runtime_core(const char *name, const AnkusGucDefinitionRequest *input)
        {
            for (int index = 0; index < ankus_guc_count; index++)
            {
                if (ankus_guc_name_equal(ankus_guc_definitions[index]->name_utf8, name))
                    ereport(ERROR, (errcode(ERRCODE_DUPLICATE_OBJECT),
                        errmsg("configuration parameter \"%s\" is already declared by a PgGuc attribute", name)));
            }

            AnkusGuc *existing = ankus_guc_runtime_find(name);
            if (existing != NULL)
            {
                if (!ankus_guc_runtime_same(existing, input))
                    ereport(ERROR, (errcode(ERRCODE_DUPLICATE_OBJECT),
                        errmsg("configuration parameter \"%s\" is already defined with different properties", name)));
                ankus_guc_register(existing);
                return;
            }

            if (input->hooks < 0 || input->hooks > 7 ||
                (input->hooks != 0 && (input->slot < 1 || input->slot > ANKUS_GUC_RUNTIME_SLOTS || ankus_guc_runtime_slots[input->slot] != NULL)))
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("invalid hook slot for configuration parameter \"%s\"", name)));

            AnkusRuntimeGuc *runtime = ankus_guc_allocate(sizeof(AnkusRuntimeGuc));
            memset(runtime, 0, sizeof(AnkusRuntimeGuc));
            AnkusGuc *definition = &runtime->definition;
            definition->name_utf8 = ankus_guc_runtime_text(name);
            definition->short_utf8 = ankus_guc_runtime_text(input->short_utf8);
            definition->long_utf8 = ankus_guc_runtime_text(input->long_utf8);
            definition->kind = input->kind;
            definition->context = input->context;
            definition->flags = input->flags;
            definition->unit = input->unit;
            switch (input->kind)
            {
                case 0:
                    definition->variable = ankus_guc_allocate(sizeof(bool));
                    definition->boot.boolean = input->boot_boolean != 0;
                    *((bool *) definition->variable) = definition->boot.boolean;
                    break;
                case 1:
                    definition->variable = ankus_guc_allocate(sizeof(int));
                    definition->boot.integer = input->boot_integer;
                    definition->minimum.integer = input->minimum_integer;
                    definition->maximum.integer = input->maximum_integer;
                    *((int *) definition->variable) = definition->boot.integer;
                    break;
                case 2:
                    definition->variable = ankus_guc_allocate(sizeof(double));
                    definition->boot.real = input->boot_real;
                    definition->minimum.real = input->minimum_real;
                    definition->maximum.real = input->maximum_real;
                    *((double *) definition->variable) = definition->boot.real;
                    break;
                case 3:
                    definition->variable = ankus_guc_allocate(sizeof(char *));
                    *((char **) definition->variable) = NULL;
                    definition->boot.string = input->boot_null ? NULL : ankus_guc_runtime_text(input->boot_string);
                    break;
                case 4:
                {
                    definition->variable = ankus_guc_allocate(sizeof(int));
                    definition->boot.integer = input->boot_integer;
                    *((int *) definition->variable) = definition->boot.integer;
                    struct config_enum_entry *options =
                        ankus_guc_allocate(sizeof(struct config_enum_entry) * (Size) (input->option_count + 1));
                    memset(options, 0, sizeof(struct config_enum_entry) * (Size) (input->option_count + 1));
                    for (int index = 0; index < input->option_count; index++)
                    {
                        options[index].name = ankus_guc_runtime_text(input->option_names[index]);
                        options[index].val = input->option_values[index];
                        options[index].hidden = input->option_hidden[index] != 0;
                    }

                    definition->options_utf8 = options;
                    break;
                }
                default: elog(ERROR, "invalid Ankus configuration kind");
            }

            if (input->hooks != 0)
                ankus_guc_runtime_bind(definition, input->hooks, input->slot);

            /* Link before registering: PostgreSQL may retain the storage even if registration then
             * fails, and an identical retry must reuse it rather than define a second owner. */
            runtime->next = ankus_guc_runtime;
            ankus_guc_runtime = runtime;
            ankus_guc_register(definition);
        }
        """;

    /// <summary>
    /// Gets the prebuilt hook functions for run-time definitions, emitted after the managed hook source they call.
    /// </summary>
    /// <remarks>
    /// PostgreSQL's hook signatures carry no parameter identity, so each slot has its own check, assign and show
    /// functions that pass the slot's definition to the shared hook path; the managed dispatcher then selects the
    /// delegates registered for that slot.
    /// </remarks>
    internal static string RuntimeHooks => s_runtimeHooks;

    /// <summary>
    /// The composed run-time hook source.
    /// </summary>
    private static readonly string s_runtimeHooks = """
        extern int ankus_guc_runtime_hook(int, AnkusValue *, AnkusValue *, int, AnkusError *, AnkusGucRead, AnkusExecute, AnkusGucLog,
            AnkusMemoryApi *);

        #define ANKUS_GUC_RUNTIME_SLOT(n) \
            static bool ankus_guc_runtime_check_bool_##n(bool *value, void **extra, GucSource source) \
            { return ankus_guc_check(ankus_guc_runtime_slots[n], value, extra, source); } \
            static bool ankus_guc_runtime_check_int_##n(int *value, void **extra, GucSource source) \
            { return ankus_guc_check(ankus_guc_runtime_slots[n], value, extra, source); } \
            static bool ankus_guc_runtime_check_real_##n(double *value, void **extra, GucSource source) \
            { return ankus_guc_check(ankus_guc_runtime_slots[n], value, extra, source); } \
            static bool ankus_guc_runtime_check_string_##n(char **value, void **extra, GucSource source) \
            { return ankus_guc_check(ankus_guc_runtime_slots[n], value, extra, source); } \
            static void ankus_guc_runtime_assign_bool_##n(bool value, void *extra) \
            { ankus_guc_assign(ankus_guc_runtime_slots[n], &value, extra); } \
            static void ankus_guc_runtime_assign_int_##n(int value, void *extra) \
            { ankus_guc_assign(ankus_guc_runtime_slots[n], &value, extra); } \
            static void ankus_guc_runtime_assign_real_##n(double value, void *extra) \
            { ankus_guc_assign(ankus_guc_runtime_slots[n], &value, extra); } \
            static void ankus_guc_runtime_assign_string_##n(const char *value, void *extra) \
            { ankus_guc_assign(ankus_guc_runtime_slots[n], &value, extra); } \
            static const char *ankus_guc_runtime_show_##n(void) \
            { return ankus_guc_show(ankus_guc_runtime_slots[n]); }

        """ + string.Concat(Enumerable.Range(1, 64).Select(static slot => $"ANKUS_GUC_RUNTIME_SLOT({slot})\n")) + "\n" + Table("GucBoolCheckHook", "check_bool") +
        Table("GucIntCheckHook", "check_int") + Table("GucRealCheckHook", "check_real") + Table("GucStringCheckHook", "check_string") +
        Table("GucBoolAssignHook", "assign_bool") + Table("GucIntAssignHook", "assign_int") + Table("GucRealAssignHook", "assign_real") +
        Table("GucStringAssignHook", "assign_string") + Table("GucShowHook", "show") + """

        /* Enumerated settings use the integer hook shapes, as PostgreSQL's enum hooks take and return an int. */
        static void
        ankus_guc_runtime_bind(AnkusGuc *definition, int hooks, int slot)
        {
            definition->hook = ankus_guc_runtime_hook;
            definition->runtime_slot = slot;
            definition->has_check = (hooks & 1) != 0;
            definition->has_assign = (hooks & 2) != 0;
            definition->has_show = (hooks & 4) != 0;
            switch (definition->kind)
            {
                case 0:
                    definition->assign.boolean = ankus_guc_runtime_assign_bool[slot];
                    if (definition->has_check) definition->check.boolean = ankus_guc_runtime_check_bool[slot];
                    break;
                case 1:
                    definition->assign.integer = ankus_guc_runtime_assign_int[slot];
                    if (definition->has_check) definition->check.integer = ankus_guc_runtime_check_int[slot];
                    break;
                case 2:
                    definition->assign.real = ankus_guc_runtime_assign_real[slot];
                    if (definition->has_check) definition->check.real = ankus_guc_runtime_check_real[slot];
                    break;
                case 3:
                    definition->assign.string = ankus_guc_runtime_assign_string[slot];
                    if (definition->has_check) definition->check.string = ankus_guc_runtime_check_string[slot];
                    break;
                default:
                    definition->assign.enumeration = ankus_guc_runtime_assign_int[slot];
                    if (definition->has_check) definition->check.enumeration = ankus_guc_runtime_check_int[slot];
                    break;
            }

            if (definition->has_show)
                definition->show = ankus_guc_runtime_show[slot];
            ankus_guc_runtime_slots[slot] = definition;
        }
        """;

    /// <summary>
    /// Declares the table of one prebuilt hook kind, indexed by slot, with slot zero unused.
    /// </summary>
    /// <param name="type">The PostgreSQL hook type.</param>
    /// <param name="name">The hook kind in the function names.</param>
    /// <returns>The C table definition.</returns>
    private static string Table(string type, string name)
        => $"static const {type} ankus_guc_runtime_{name}[] = {{ NULL, " +
            string.Join(", ", Enumerable.Range(1, 64).Select(slot => $"ankus_guc_runtime_{name}_{slot}")) + " };\n";

    /// <summary>
    /// Gets the guarded entry point for run-time definitions, emitted after the read source's error capture.
    /// </summary>
    internal const string RuntimeDefinitionEntry = """
        static int
        ankus_guc_define_runtime(const char *name, intptr_t request, AnkusError *error)
        {
            if (ankus_recovery_failed(error))
                return 1;

            MemoryContext caller = CurrentMemoryContext;
            volatile int status = 0;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    ankus_guc_define_runtime_core(name, (const AnkusGucDefinitionRequest *) request);
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(caller);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    ankus_guc_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    status = 1;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(caller);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover an Ankus configuration definition failure")));
            }
            PG_END_TRY();
            MemoryContextSwitchTo(caller);
            if (status != 0)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            return status;
        }
        """;

    /// <summary>
    /// Gets the optional fast read binding included in every full native backend bridge.
    /// </summary>
    internal const string ReadBinding = """
        typedef int (*AnkusGucReadBinding)(const char *, int, AnkusValue *, AnkusError *);
        static AnkusGucReadBinding ankus_read_guc = NULL;
        /* Read-binding kind that defines a run-time parameter; the value argument carries its definition. */
        #ifndef ANKUS_GUC_DEFINE_KIND
        #define ANKUS_GUC_DEFINE_KIND 100
        #endif
        /* Read-binding kinds from here on return a declared scalar's variable address instead of its value. */
        #ifndef ANKUS_GUC_LOCATE_KIND
        #define ANKUS_GUC_LOCATE_KIND 200
        #endif
        """;

    /// <summary>
    /// Gets only the hook forward declarations needed by the generated wrappers, plus the native reader.
    /// </summary>
    /// <param name="hasCheck">Whether any declaration has a check hook.</param>
    /// <param name="hasAssign">Whether generated assignment wrappers track configuration state.</param>
    /// <param name="hasShow">Whether any declaration has a show hook.</param>
    /// <returns>The required native callback prototypes.</returns>
    internal static string GetManagedDeclarations(bool hasCheck, bool hasAssign, bool hasShow)
        => (hasCheck || hasAssign || hasShow ? """
            static bool ankus_registration_complete = false;
            static void ankus_ensure_initialized(void);
            static void ankus_guc_complete_worker_restore(void);
            """ : string.Empty) +
            "static int ankus_guc_read(const char *, int, AnkusValue *, AnkusError *);\n" +
            (hasCheck ? "static bool ankus_guc_check(AnkusGuc *, void *, void **, GucSource);\n" : string.Empty) +
            (hasAssign ? "static void ankus_guc_assign(AnkusGuc *, const void *, void *);\n" : string.Empty) +
            (hasShow ? "static const char *ankus_guc_show(AnkusGuc *);\n" : string.Empty);

    /// <summary>
    /// Emits typed reads and exactly the native helper dependencies required by the declared hooks.
    /// </summary>
    /// <param name="hasCheck">Whether any declaration has a check hook.</param>
    /// <param name="hasAssign">Whether generated assignment wrappers track configuration state.</param>
    /// <param name="hasShow">Whether any declaration has a show hook.</param>
    /// <returns>The selected native implementation fragments.</returns>
    internal static string GetManagedSource(bool hasCheck, bool hasAssign, bool hasShow)
        => ReadSource +
            (hasCheck || hasAssign || hasShow ? "\n\n" + HookCommon : string.Empty) +
            (hasCheck || hasShow ? "\n\n" + PersistentResult : string.Empty) +
            (hasCheck ? "\n\n" + Check : string.Empty) +
            (hasAssign ? "\n\n" + Assign : string.Empty) +
            (hasShow ? "\n\n" + Show : string.Empty) +
            (hasCheck || hasAssign || hasShow ? "\n\n" + WorkerRestore : string.Empty);

    /// <summary>
    /// Provides owned typed reads, cached conversion functions, and native error capture.
    /// </summary>
    private const string ReadSource = """
        #if PG_VERSION_NUM >= 160000
        static bool
        ankus_guc_name_equal(const char *left, const char *right)
        {
            while (*left != 0 && *right != 0)
            {
                unsigned char a = (unsigned char) *left++;
                unsigned char b = (unsigned char) *right++;
                if (a >= 'A' && a <= 'Z') a += 'a' - 'A';
                if (b >= 'A' && b <= 'Z') b += 'a' - 'A';
                if (a != b) return false;
            }

            return *left == *right;
        }
        #endif

        #include "catalog/namespace.h"

        static int ankus_guc_encoding = -1;
        static FmgrInfo ankus_guc_to_utf8;
        static FmgrInfo ankus_guc_from_utf8;

        static void
        ankus_guc_prepare_encoding(void)
        {
            int encoding = GetDatabaseEncoding();
            if (encoding == PG_UTF8 || encoding == PG_SQL_ASCII || ankus_guc_encoding == encoding)
                return;
            if (!IsTransactionState())
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("Ankus configuration encoding conversion was not initialized in a transaction")));
            Oid to_utf8 = FindDefaultConversionProc(encoding, PG_UTF8);
            Oid from_utf8 = FindDefaultConversionProc(PG_UTF8, encoding);
            if (!OidIsValid(to_utf8) || !OidIsValid(from_utf8))
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_FUNCTION),
                    errmsg("Ankus configuration requires database encoding conversion to and from UTF8")));
            fmgr_info_cxt(to_utf8, &ankus_guc_to_utf8, TopMemoryContext);
            fmgr_info_cxt(from_utf8, &ankus_guc_from_utf8, TopMemoryContext);
            ankus_guc_encoding = encoding;
        }

        static char *
        ankus_guc_convert(const char *text, bool to_utf8)
        {
            int encoding = GetDatabaseEncoding();
            if (ankus_guc_ascii(text))
                return (char *) text;
            if (encoding == PG_UTF8 || encoding == PG_SQL_ASCII)
            {
                pg_verify_mbstr(PG_UTF8, text, strlen(text), false);
                return (char *) text;
            }

            ankus_guc_prepare_encoding();
            Size length = strlen(text);
            if (length > (MaxAllocSize - 1) / MAX_CONVERSION_GROWTH)
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Configuration text is too large for encoding conversion")));
            char *converted = palloc(length * MAX_CONVERSION_GROWTH + 1);
            FmgrInfo *function = to_utf8 ? &ankus_guc_to_utf8 : &ankus_guc_from_utf8;
            int source = to_utf8 ? encoding : PG_UTF8;
            int target = to_utf8 ? PG_UTF8 : encoding;
        #if PG_VERSION_NUM >= 140000
            (void) FunctionCall6(function, Int32GetDatum(source), Int32GetDatum(target),
                CStringGetDatum(text), CStringGetDatum(converted), Int32GetDatum((int) length), BoolGetDatum(false));
        #else
            (void) FunctionCall5(function, Int32GetDatum(source), Int32GetDatum(target),
                CStringGetDatum(text), CStringGetDatum(converted), Int32GetDatum((int) length));
        #endif
            return converted;
        }

        static void
        ankus_guc_capture_error(ErrorData *data, AnkusError *error)
        {
            const char *fields[ANKUS_ERROR_FIELD_COUNT] = {
                data->message, data->detail, data->hint, data->context,
                data->schema_name, data->table_name, data->column_name, data->datatype_name,
                data->constraint_name, data->internalquery, data->filename, data->funcname,
                data->detail_log, data->backtrace, data->domain
            };
            error->sqlstate = data->sqlerrcode;
            error->position = data->cursorpos;
            error->internal_position = data->internalpos;
            error->line = data->lineno;
            error->flags = ANKUS_ERROR_RETHROW |
                (data->output_to_server ? ANKUS_ERROR_SERVER : 0) |
                (data->output_to_client ? ANKUS_ERROR_CLIENT : 0) |
                (data->hide_stmt ? ANKUS_ERROR_HIDE_STATEMENT : 0) |
                (data->hide_ctx ? ANKUS_ERROR_HIDE_CONTEXT : 0);
        #if PG_VERSION_NUM < 140000
            error->flags |= data->show_funcname ? ANKUS_ERROR_SHOW_FUNCTION : 0;
        #endif
            for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
            {
                if (fields[index] == NULL) continue;
                char *utf8 = ankus_guc_convert(fields[index], true);
                int length = strlen(utf8);
                AnkusValue *value = &error->fields[index];
                if (index == ANKUS_ERROR_MESSAGE)
                {
                    int clipped = pg_encoding_mbcliplen(PG_UTF8, utf8, length, sizeof(error->message) - 1);
                    memcpy(error->message, utf8, clipped);
                    error->message[clipped] = 0;
                }

                value->data = malloc((Size) length + 1);
                if (value->data != NULL)
                {
                    memcpy(value->data, utf8, (Size) length + 1);
                    value->length = length;
                    value->release = ankus_free_error_buffer;
                }
                else
                    error->flags |= ANKUS_ERROR_INCOMPLETE;
                if (utf8 != fields[index]) pfree(utf8);
            }
        }

        static void
        ankus_guc_release_value(AnkusValue *value)
        {
            if (value->release != NULL)
                value->release(value->data);
            memset(value, 0, sizeof(AnkusValue));
        }

        static void
        ankus_guc_copy_bytes(AnkusValue *value, const void *data, int length)
        {
            value->data = malloc((Size) length + 1);
            if (value->data == NULL)
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("out of memory")));
            value->length = length;
            value->release = ankus_free_error_buffer;
            if (length > 0)
                memcpy(value->data, data, length);
            value->data[length] = 0;
        }

        static void
        ankus_guc_value(AnkusGuc *definition, const void *input, AnkusValue *value)
        {
            switch (definition->kind)
            {
                case 0: value->integral = *((const bool *) input) ? 1 : 0; break;
                case 1:
                case 4: value->integral = *((const int *) input); break;
                case 2: memcpy(&value->integral, input, sizeof(double)); break;
                case 3:
                {
                    const char *text = *((const char *const *) input);
                    if (text == NULL)
                    {
                        value->is_null = 1;
                        break;
                    }

                    char *utf8 = ankus_guc_convert(text, true);
                    ankus_guc_copy_bytes(value, utf8, strlen(utf8));
                    if (utf8 != text)
                        pfree(utf8);
                    break;
                }

                default: elog(ERROR, "invalid Ankus configuration kind");
            }
        }

        static int
        ankus_guc_read(const char *name, int kind, AnkusValue *value, AnkusError *error)
        {
            if (ankus_recovery_failed(error))
                return 1;

            /* Definitions share the read binding so they work wherever reads do, including library
             * loads outside a transaction, such as session preload and parallel worker startup. */
            if (kind == ANKUS_GUC_DEFINE_KIND)
            {
        #ifdef ANKUS_GUC_RUNTIME
                return ankus_guc_define_runtime(name, (intptr_t) value, error);
        #else
                error->sqlstate = ERRCODE_FEATURE_NOT_SUPPORTED;
                strlcpy(error->message, "This extension was built without run-time configuration support", sizeof(error->message));
                return 1;
        #endif
            }

            bool locate = kind >= ANKUS_GUC_LOCATE_KIND;
            if (locate)
                kind -= ANKUS_GUC_LOCATE_KIND;

            AnkusGuc *definition = NULL;
            for (int index = 0; index < ankus_guc_count; index++)
            {
                if (ankus_guc_name_equal(ankus_guc_definitions[index]->name_utf8, name))
                {
                    definition = ankus_guc_definitions[index];
                    break;
                }
            }

        #ifdef ANKUS_GUC_RUNTIME
            /* Only declared properties locate their variables; run-time parameters are read by name. */
            if (definition == NULL && !locate)
            {
                definition = ankus_guc_runtime_find(name);
                if (definition != NULL && !definition->installed)
                {
                    error->sqlstate = ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE;
                    strlcpy(error->message, "The run-time configuration parameter was not registered", sizeof(error->message));
                    return 1;
                }
            }
        #endif

            if (definition == NULL)
            {
                error->sqlstate = ERRCODE_UNDEFINED_OBJECT;
                strlcpy(error->message, "Unknown Ankus configuration parameter", sizeof(error->message));
                return 1;
            }

            if (definition->kind != kind)
            {
                error->sqlstate = ERRCODE_DATATYPE_MISMATCH;
                strlcpy(error->message, "Ankus configuration type does not match its declaration", sizeof(error->message));
                return 1;
            }

            /* A Boolean, integer, real or enum value is a static the server writes in place, so the managed
             * property reads it directly after this lookup. A string's static holds a pointer the server
             * replaces on assignment; the property compares its bytes with the ones it last converted. */
            if (locate)
            {
                value->integral = (int64) (intptr_t) definition->variable;
                return 0;
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext volatile work = NULL;
            volatile int status = 0;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    work = AllocSetContextCreate(caller, "Ankus configuration read", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(work);
                    if (!definition->prepared && definition->kind == 3)
                    {
                        if (definition->boot.string == NULL)
                            value->is_null = 1;
                        else
                            ankus_guc_copy_bytes(value, definition->boot.string, strlen(definition->boot.string));
                    }
                    else
                        ankus_guc_value(definition, definition->variable, value);
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(caller);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    ankus_guc_release_value(value);
                    ankus_guc_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    status = 1;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(caller);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover an Ankus configuration read failure")));
            }
            PG_END_TRY();
            MemoryContextSwitchTo(caller);
            if (work != NULL)
                MemoryContextDelete(work);
            if (status != 0)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            return status;
        }
        """;

    /// <summary>
    /// Provides the shared callback frame, owned argument data, diagnostic reporting, and cleanup.
    /// </summary>
    private const string HookCommon = """
        static bool ankus_guc_replaying_worker_restore = false;

        static bool
        ankus_guc_worker_restore_in_progress(void)
        {
        #if PG_VERSION_NUM < 180000
            return InitializingParallelWorker;
        #else
            return false;
        #endif
        }

        static void
        ankus_guc_ensure_managed_ready(void)
        {
            if (!ankus_guc_replaying_worker_restore && ankus_registration_complete && !ankus_module_loading)
                ankus_ensure_initialized();
            else
                ankus_guc_complete_worker_restore();
        }

        typedef struct AnkusGucExtra
        {
            int32 length;
            unsigned char data[FLEXIBLE_ARRAY_MEMBER];
        } AnkusGucExtra;

        typedef struct AnkusGucFrame
        {
            /* The current or proposed value, its extra data, and the run-time hook slot. */
            AnkusValue arguments[3];
            AnkusValue results[2];
            AnkusError error;
            void *pending_extra;
            char *pending_string;
            bool snapshot_owned;
            bool fork_entered;
        } AnkusGucFrame;

        static void
        ankus_guc_free(void *value)
        {
            if (value == NULL)
                return;
        #if PG_VERSION_NUM >= 160000
            guc_free(value);
        #else
            free(value);
        #endif
        }

        static char *
        ankus_guc_error_field(AnkusError *error, enum AnkusDiagnosticField field)
        {
            const char *utf8 = (const char *) error->fields[field].data;
            if (utf8 == NULL) return NULL;
            char *converted = ankus_guc_convert(utf8, false);
            char *copy = pstrdup(converted);
            if (converted != utf8) pfree(converted);
            return copy;
        }

        static void
        ankus_guc_report(AnkusError *error, int level)
        {
            ErrorData data = {0};
            data.elevel = level;
            data.sqlerrcode = error->sqlstate;
            data.cursorpos = error->position;
            data.internalpos = error->internal_position;
            data.lineno = error->line;
            data.output_to_server = (error->flags & ANKUS_ERROR_SERVER) != 0;
            data.output_to_client = (error->flags & ANKUS_ERROR_CLIENT) != 0;
            data.hide_stmt = (error->flags & ANKUS_ERROR_HIDE_STATEMENT) != 0;
            data.hide_ctx = (error->flags & ANKUS_ERROR_HIDE_CONTEXT) != 0;
        #if PG_VERSION_NUM < 140000
            data.show_funcname = (error->flags & ANKUS_ERROR_SHOW_FUNCTION) != 0;
        #endif
            data.message = ankus_guc_error_field(error, ANKUS_ERROR_MESSAGE);
            if (data.message == NULL)
                data.message = ankus_guc_convert(error->message, false);
            data.detail = ankus_guc_error_field(error, ANKUS_ERROR_DETAIL);
            data.hint = ankus_guc_error_field(error, ANKUS_ERROR_HINT);
            data.context = ankus_guc_error_field(error, ANKUS_ERROR_CONTEXT);
            data.schema_name = ankus_guc_error_field(error, ANKUS_ERROR_SCHEMA);
            data.table_name = ankus_guc_error_field(error, ANKUS_ERROR_TABLE);
            data.column_name = ankus_guc_error_field(error, ANKUS_ERROR_COLUMN);
            data.datatype_name = ankus_guc_error_field(error, ANKUS_ERROR_DATATYPE);
            data.constraint_name = ankus_guc_error_field(error, ANKUS_ERROR_CONSTRAINT);
            data.internalquery = ankus_guc_error_field(error, ANKUS_ERROR_QUERY);
            data.filename = ankus_guc_error_field(error, ANKUS_ERROR_FILE);
            data.funcname = ankus_guc_error_field(error, ANKUS_ERROR_ROUTINE);
            data.detail_log = ankus_guc_error_field(error, ANKUS_ERROR_DETAIL_LOG);
            data.backtrace = ankus_guc_error_field(error, ANKUS_ERROR_BACKTRACE);
            /* ErrorData source locations must survive caller-context cleanup after ERROR. */
            data.filename = data.filename == NULL ? __FILE__ :
                (level >= ERROR ? MemoryContextStrdup(ErrorContext, data.filename) : data.filename);
            data.funcname = data.funcname == NULL ? "ankus_guc_report" :
                (level >= ERROR ? MemoryContextStrdup(ErrorContext, data.funcname) : data.funcname);
            data.assoc_context = CurrentMemoryContext;
            if (level == ERROR && (error->flags & ANKUS_ERROR_RETHROW) != 0)
                ReThrowError(&data);
            ThrowErrorData(&data);
        }

        static int
        ankus_guc_log(int operation, int level, AnkusError *report, AnkusError *error, int *enabled)
        {
            if (operation == 2)
                return ankus_recovery_terminal(level, report, error);

            if (ankus_recovery_failed(error))
            {
                if (error->report_level < 12 || (error->flags & ANKUS_ERROR_UNRECOVERED) != 0)
                    return 1;
                memset(error, 0, sizeof(*error));
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext volatile work = NULL;
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            volatile int status = 0;
            volatile bool flushed = false;
            if (operation == 1 && level >= 0 && level < 10)
            {
                HOLD_INTERRUPTS();
            }

            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    if ((operation != 0 && operation != 1) || level < 0 || level > 12 || enabled == NULL)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid configuration logging request")));
                    if (operation == 1 && (level >= 10 || report == NULL))
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Terminal configuration messages must unwind managed code before reporting")));
                    *enabled = ankus_log_enabled(ankus_log_level(level), error) ? 1 : 0;
                    status = error->sqlstate != 0;
                    if (status == 0 && operation == 1 && *enabled != 0)
                    {
                        work = AllocSetContextCreate(caller, "Ankus configuration logging", ALLOCSET_SMALL_SIZES);
                        MemoryContextSwitchTo(work);
                        ankus_guc_report(report, ankus_log_level(level));
                    }
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(caller);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    flushed = true;
                    ankus_guc_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    status = 1;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(caller);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover an Ankus configuration logging failure")));
            }
            PG_END_TRY();
            MemoryContextSwitchTo(caller);
            if (work != NULL)
                MemoryContextDelete(work);
            if (flushed)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
            QueryCancelHoldoffCount = cancel_holdoff;
            return status;
        }

        static void
        ankus_guc_frame_release(AnkusGucFrame *frame)
        {
            if (frame->snapshot_owned)
            {
                frame->snapshot_owned = false;
                PopActiveSnapshot();
            }

            for (int index = 0; index < 2; index++)
            {
                ankus_guc_release_value(&frame->arguments[index]);
                ankus_guc_release_value(&frame->results[index]);
            }

            ankus_guc_release_value(&frame->arguments[2]);

            ankus_release_error(&frame->error);
            ankus_guc_free(frame->pending_extra);
            ankus_guc_free(frame->pending_string);
            bool fork_entered = frame->fork_entered;
            pfree(frame);
            if (fork_entered)
            {
                ankus_fork_host_exit();
            }
        }

        static void
        ankus_guc_arguments(AnkusGuc *definition, const void *input, const void *extra, AnkusGucFrame *frame)
        {
            ankus_guc_value(definition, input, &frame->arguments[0]);
            frame->arguments[2].integral = definition->runtime_slot;
            if (extra == NULL)
                frame->arguments[1].is_null = 1;
            else
            {
                const AnkusGucExtra *payload = extra;
                ankus_guc_copy_bytes(&frame->arguments[1], payload->data, payload->length);
            }
        }
        """;

    /// <summary>
    /// Copies accepted check and show strings into allocator-matched native configuration storage.
    /// </summary>
    private const string PersistentResult = """
        static char *
        ankus_guc_persistent_result(const char *utf8)
        {
            char *converted = ankus_guc_convert(utf8, false);
            Size length = strlen(converted) + 1;
            char *copy = ankus_guc_allocate(length);
            memcpy(copy, converted, length);
            if (converted != utf8) pfree(converted);
            return copy;
        }
        """;

    /// <summary>
    /// Checks and normalizes proposed values with native GUC rejection semantics.
    /// </summary>
    private const string Check = """
        static int
        ankus_guc_source(GucSource source)
        {
            switch (source)
            {
                case PGC_S_DEFAULT: return 0;
                case PGC_S_DYNAMIC_DEFAULT: return 1;
                case PGC_S_ENV_VAR: return 2;
                case PGC_S_FILE: return 3;
                case PGC_S_ARGV: return 4;
                case PGC_S_GLOBAL: return 5;
                case PGC_S_DATABASE: return 6;
                case PGC_S_USER: return 7;
                case PGC_S_DATABASE_USER: return 8;
                case PGC_S_CLIENT: return 9;
                case PGC_S_OVERRIDE: return 10;
                case PGC_S_INTERACTIVE: return 11;
                case PGC_S_TEST: return 12;
                case PGC_S_SESSION: return 13;
                default: elog(ERROR, "invalid PostgreSQL configuration source"); return 0;
            }
        }

        static void
        ankus_guc_reject(AnkusError *error)
        {
            GUC_check_errcode(error->sqlstate == 0 ? ERRCODE_INVALID_PARAMETER_VALUE : error->sqlstate);
            GUC_check_errmsg_string = NULL;
            GUC_check_errdetail_string = NULL;
            GUC_check_errhint_string = NULL;
            char *message = ankus_guc_error_field(error, ANKUS_ERROR_MESSAGE);
            char *detail = ankus_guc_error_field(error, ANKUS_ERROR_DETAIL);
            char *hint = ankus_guc_error_field(error, ANKUS_ERROR_HINT);
            if (message != NULL)
                GUC_check_errmsg("%s", message);
            else if (error->message[0] != 0)
            {
                char *converted = ankus_guc_convert(error->message, false);
                GUC_check_errmsg("%s", converted);
                if (converted != error->message) pfree(converted);
            }

            if (detail != NULL) GUC_check_errdetail("%s", detail);
            if (hint != NULL) GUC_check_errhint("%s", hint);
            if (message != NULL) pfree(message);
            if (detail != NULL) pfree(detail);
            if (hint != NULL) pfree(hint);
        }

        static void
        ankus_guc_accept(AnkusGuc *definition, void *proposed, void **extra, AnkusGucFrame *frame)
        {
            AnkusValue *value = &frame->results[0];
            if (value->is_null && definition->kind != 3)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A scalar configuration check returned NULL")));
            switch (definition->kind)
            {
                case 0:
                    if (value->integral != 0 && value->integral != 1)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A Boolean configuration check returned an invalid value")));
                    break;
                case 1:
                    if (value->integral < INT_MIN || value->integral > INT_MAX)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A configuration check returned an unrepresentable integer")));
                    break;
                case 2:
                    /* Native check hooks may replace the parsed proposal with any representable double bits. */
                    break;

                case 3:
                    if (!value->is_null)
                    {
                        if (value->data == NULL || value->length < 0 || memchr(value->data, 0, value->length) != NULL)
                            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A configuration check returned invalid string data")));
                        frame->pending_string = ankus_guc_persistent_result((char *) value->data);
                    }

                    break;
                case 4:
                {
                    bool valid = false;
                    for (const struct config_enum_entry *option = definition->options; option->name != NULL; option++)
                        valid = valid || option->val == value->integral;
                    if (!valid)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A configuration check returned an unknown enumeration value")));
                    break;
                }
            }

            AnkusValue *payload = &frame->results[1];
            if (!payload->is_null)
            {
                if (payload->length < 0 || (payload->length > 0 && payload->data == NULL))
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A configuration check returned invalid extra data")));
                AnkusGucExtra *copy = ankus_guc_allocate(offsetof(AnkusGucExtra, data) + (Size) payload->length);
                frame->pending_extra = copy;
                copy->length = payload->length;
                if (payload->length > 0)
                    memcpy(copy->data, payload->data, payload->length);
            }

            switch (definition->kind)
            {
                case 0: *((bool *) proposed) = value->integral != 0; break;
                case 1:
                case 4: *((int *) proposed) = (int) value->integral; break;
                case 2: memcpy(proposed, &value->integral, sizeof(double)); break;
                case 3:
                    ankus_guc_free(*((char **) proposed));
                    *((char **) proposed) = frame->pending_string;
                    frame->pending_string = NULL;
                    break;
            }

            *extra = frame->pending_extra;
            frame->pending_extra = NULL;
        }

        static bool
        ankus_guc_check_core(AnkusGuc *definition, void *proposed, void **extra, GucSource source, bool raise)
        {
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext work = AllocSetContextCreate(caller, "Ankus configuration check", ALLOCSET_SMALL_SIZES);
            MemoryContextSwitchTo(work);
            AnkusMemoryApi memory = {0};
            ankus_memory_initialize(&memory);
            AnkusGucFrame *frame = palloc0(sizeof(AnkusGucFrame));
            volatile bool accepted = false;
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    ankus_guc_arguments(definition, proposed, NULL, frame);
                    bool transactional = IsTransactionState();
                    if (transactional && !ActiveSnapshotSet())
                    {
                        PushActiveSnapshot(GetTransactionSnapshot());
                        frame->snapshot_owned = true;
                    }

                    ankus_fork_host_enter();
                    frame->fork_entered = true;
                    int status;
                    ANKUS_MANAGED_INVOKE(status, &frame->error, definition->hook(0, frame->arguments, frame->results, ankus_guc_source(source),
                        &frame->error, ankus_guc_read, transactional ? ankus_spi_execute : NULL, ankus_guc_log, &memory));
                    if (frame->snapshot_owned)
                    {
                        frame->snapshot_owned = false;
                        PopActiveSnapshot();
                    }

                    if (status == 0)
                    {
                        ankus_guc_accept(definition, proposed, extra, frame);
                        accepted = true;
                    }
                    else if (frame->error.report_level != 0)
                        ankus_guc_report(&frame->error, ankus_log_level(frame->error.report_level - 1));
                    /* A check that throws while PostgreSQL installs the setting fails the
                     * registration, as a C hook's ERROR would; a returned rejection keeps
                     * PostgreSQL's terminal boot-value policy. */
                    else if (!raise && (frame->error.flags & ANKUS_ERROR_UNRECOVERED) == 0 &&
                        frame->error.sqlstate != ERRCODE_QUERY_CANCELED &&
                        !(status == 1 && source == PGC_S_DEFAULT && ankus_guc_defining == definition))
                        ankus_guc_reject(&frame->error);
                    else
                    {
                        frame->error.flags |= ANKUS_ERROR_SERVER | ANKUS_ERROR_CLIENT;
                        ankus_guc_report(&frame->error, ERROR);
                    }
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(caller);
                    if (frame->snapshot_owned)
                    {
                        frame->snapshot_owned = false;
                        PopActiveSnapshot();
                    }

                    /* Returning false is reserved for managed validation rejection.
                     * A native conversion, snapshot or callback ERROR has not been
                     * rolled back and must propagate to PostgreSQL's error handler. */
                    if (frame->error.report_level != 0)
                    {
                        ErrorData *data = ankus_copy_error_data();
                        FlushErrorState();
                        data->elevel = ankus_log_level(frame->error.report_level - 1);
                        ThrowErrorData(data);
                    }

                    PG_RE_THROW();
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                ankus_guc_frame_release(frame);
                MemoryContextDelete(work);
            }
            PG_END_TRY();
            return accepted;
        }

        static bool
        ankus_guc_check(AnkusGuc *definition, void *proposed, void **extra, GucSource source)
        {
            if (ankus_guc_worker_restore_in_progress())
            {
                definition->worker_restore_pending = true;
                *extra = NULL;
                return true;
            }

            ankus_guc_ensure_managed_ready();
            return ankus_guc_check_core(definition, proposed, extra, source, false);
        }
        """;

    /// <summary>
    /// Runs assignment callbacks with terminal failure handling and native extra tracking.
    /// </summary>
    private const string Assign = """
        static void
        ankus_guc_assign(AnkusGuc *definition, const void *accepted, void *extra)
        {
            if (ankus_guc_worker_restore_in_progress())
            {
                definition->worker_restore_pending = true;
                definition->extra = extra;
                return;
            }

            ankus_guc_ensure_managed_ready();
            if (!definition->has_assign)
            {
                definition->extra = extra;
                return;
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext volatile work = NULL;
            AnkusGucFrame *volatile frame = NULL;
            /* Transaction abort rolls settings back through assign hooks, sometimes while a
             * caller's error is still pending. */
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    work = AllocSetContextCreate(caller, "Ankus configuration assignment", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(work);
                    AnkusMemoryApi memory = {0};
                    ankus_memory_initialize(&memory);
                    frame = palloc0(sizeof(AnkusGucFrame));
                    ankus_guc_arguments(definition, accepted, extra, frame);
                    ankus_fork_host_enter();
                    frame->fork_entered = true;
                    int status;
                    ANKUS_MANAGED_INVOKE(status, &frame->error, definition->hook(1, frame->arguments, frame->results, 0,
                        &frame->error, ankus_guc_read, NULL, ankus_guc_log, &memory));
                    if (status != 0)
                        ankus_guc_report(&frame->error, frame->error.report_level == 0 ? FATAL :
                            ankus_log_level(frame->error.report_level - 1));
                    definition->extra = extra;
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(caller);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    data->elevel = frame == NULL || frame->error.report_level == 0 ? FATAL :
                        ankus_log_level(frame->error.report_level - 1);
                    /* A report below ERROR returns here, so the caller's error must be pending again first. */
                    AnkusPendingError *restored = pending;
                    pending = NULL;
                    ankus_pending_error_restore(restored);
                    ThrowErrorData(data);
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                if (frame != NULL)
                    ankus_guc_frame_release(frame);
                if (work != NULL)
                    MemoryContextDelete(work);
                ankus_pending_error_release(pending);
            }
            PG_END_TRY();
        }
        """;

    /// <summary>
    /// Formats current settings with a retained display buffer and phase-safe error handling.
    /// </summary>
    private const string Show = """
        static const char *
        ankus_guc_show(AnkusGuc *definition)
        {
            ankus_guc_ensure_managed_ready();
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext volatile work = NULL;
            AnkusGucFrame *volatile frame = NULL;
            char *output = NULL;
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    work = AllocSetContextCreate(caller, "Ankus configuration display", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(work);
                    AnkusMemoryApi memory = {0};
                    ankus_memory_initialize(&memory);
                    frame = palloc0(sizeof(AnkusGucFrame));
                    ankus_guc_arguments(definition, definition->variable, definition->extra, frame);
                    ankus_fork_host_enter();
                    frame->fork_entered = true;
                    int status;
                    ANKUS_MANAGED_INVOKE(status, &frame->error, definition->hook(2, frame->arguments, frame->results, 0,
                        &frame->error, ankus_guc_read, NULL, ankus_guc_log, &memory));
                    if (status != 0)
                        ankus_guc_report(&frame->error, frame->error.report_level == 0 ?
                            (IsTransactionState() ? ERROR : FATAL) : ankus_log_level(frame->error.report_level - 1));
                    AnkusValue *value = &frame->results[0];
                    if (value->is_null || value->data == NULL)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A configuration show hook returned NULL")));
                    frame->pending_string = ankus_guc_persistent_result((char *) value->data);
                    ankus_guc_free(definition->show_buffer);
                    output = definition->show_buffer = frame->pending_string;
                    frame->pending_string = NULL;
                }
                ANKUS_PG_CATCH_INNER();
                {
                    if ((frame != NULL && frame->error.report_level != 0) || !IsTransactionState())
                    {
                        MemoryContextSwitchTo(caller);
                        ErrorData *data = ankus_copy_error_data();
                        FlushErrorState();
                        data->elevel = frame == NULL || frame->error.report_level == 0 ? FATAL :
                            ankus_log_level(frame->error.report_level - 1);
                        ThrowErrorData(data);
                    }

                    PG_RE_THROW();
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                if (frame != NULL)
                    ankus_guc_frame_release(frame);
                if (work != NULL)
                    MemoryContextDelete(work);
            }
            PG_END_TRY();
            return output;
        }
        """;

    /// <summary>
    /// Reconstructs worker hook data and retains checked values in native transaction history.
    /// </summary>
    private const string WorkerRestore = """
        static bool
        ankus_guc_string_referenced(struct config_generic *setting, const char *value)
        {
            struct config_string *body = ANKUS_GUC_RECORD(setting, string);
            if (value == *body->variable || value == body->reset_val || value == body->boot_val)
            {
                return true;
            }

            for (GucStack *stack = setting->stack; stack != NULL; stack = stack->prev)
            {
                if (value == stack->prior.val.stringval || value == stack->masked.val.stringval)
                {
                    return true;
                }
            }

            return false;
        }

        static bool
        ankus_guc_extra_referenced(struct config_generic *setting, const void *extra)
        {
            if (extra == setting->extra)
            {
                return true;
            }

        #if PG_VERSION_NUM >= 190000
            void *reset = setting->reset_extra;
        #else
            void *reset = NULL;
            switch (setting->vartype)
            {
                case PGC_BOOL: reset = ((struct config_bool *) setting)->reset_extra; break;
                case PGC_INT: reset = ((struct config_int *) setting)->reset_extra; break;
                case PGC_REAL: reset = ((struct config_real *) setting)->reset_extra; break;
                case PGC_STRING: reset = ((struct config_string *) setting)->reset_extra; break;
                case PGC_ENUM: reset = ((struct config_enum *) setting)->reset_extra; break;
            }
        #endif

            if (extra == reset)
            {
                return true;
            }

            for (GucStack *stack = setting->stack; stack != NULL; stack = stack->prev)
            {
                if (extra == stack->prior.extra || extra == stack->masked.extra)
                {
                    return true;
                }
            }

            return false;
        }

        static void
        ankus_guc_retain_worker_baseline(struct config_generic *setting, GucStack *previous)
        {
            GucStack *stack = setting->stack;
            if (stack == previous)
            {
                return;
            }

            /* Replaying a session value can push its unchecked restoration state.
             * An abort must restore the checked worker value and matching extra,
             * including any normalization performed by the worker's check hook. */
            config_var_value old = stack->prior;
            config_var_value current = {0};
            switch (setting->vartype)
            {
                case PGC_BOOL: current.val.boolval = *ANKUS_GUC_RECORD(setting, bool)->variable; break;
                case PGC_INT: current.val.intval = *ANKUS_GUC_RECORD(setting, int)->variable; break;
                case PGC_REAL: current.val.realval = *ANKUS_GUC_RECORD(setting, real)->variable; break;
                case PGC_STRING: current.val.stringval = *ANKUS_GUC_RECORD(setting, string)->variable; break;
                case PGC_ENUM: current.val.enumval = *ANKUS_GUC_RECORD(setting, enum)->variable; break;
            }

            current.extra = setting->extra;
            stack->prior = current;
            stack->source = setting->source;
            stack->scontext = setting->scontext;
        #if PG_VERSION_NUM >= 150000
            stack->srole = setting->srole;
        #endif
            if (setting->vartype == PGC_STRING && old.val.stringval != NULL &&
                !ankus_guc_string_referenced(setting, old.val.stringval))
            {
                ankus_guc_free(old.val.stringval);
            }

            if (old.extra != NULL && !ankus_guc_extra_referenced(setting, old.extra))
            {
                ankus_guc_free(old.extra);
            }
        }

        static const char *
        ankus_guc_current_value(AnkusGuc *definition, struct config_generic *existing, char *buffer, Size size)
        {
            switch (definition->kind)
            {
                case 0:
                    return *((bool *) definition->variable) ? "true" : "false";
                case 1:
                    snprintf(buffer, size, "%d", *((int *) definition->variable));
                    return buffer;
                case 2:
                    snprintf(buffer, size, "%.17e", *((double *) definition->variable));
                    return buffer;
                case 3:
                {
                    const char *value = *((char **) definition->variable);
                    return value == NULL && existing->source != PGC_S_DEFAULT ? "" : value;
                }
                case 4:
        #if PG_VERSION_NUM >= 190000
                    return config_enum_lookup_by_value(existing,
        #else
                    return config_enum_lookup_by_value((struct config_enum *) existing,
        #endif
                        *((int *) definition->variable));
                default:
                    elog(ERROR, "invalid Ankus configuration kind");
                    return NULL;
            }
        }

        static void
        ankus_guc_worker_restore_error_context(void *argument)
        {
            AnkusGuc *definition = (AnkusGuc *) argument;
            errcontext("configuration parameter \"%s\"", definition->name);
        }

        static void
        ankus_guc_complete_worker_restore(void)
        {
            if (ankus_guc_replaying_worker_restore || ankus_guc_worker_restore_in_progress())
                return;
            bool pending = false;
            for (int index = 0; index < ankus_guc_count; index++)
                pending = pending || ankus_guc_definitions[index]->worker_restore_pending;
            if (!pending)
                return;

            ankus_guc_replaying_worker_restore = true;
            PG_TRY();
            {
                for (int index = 0; index < ankus_guc_count; index++)
                {
                    AnkusGuc *definition = ankus_guc_definitions[index];
                    if (!definition->worker_restore_pending)
                        continue;
                    struct config_generic *existing = ankus_guc_find(definition->name);
                    if (!ankus_guc_is_owned(definition, existing))
                        ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                            errmsg("Ankus configuration ownership changed during parallel worker restore")));
                    char buffer[128];
                    const char *value = ankus_guc_current_value(definition, existing, buffer, sizeof(buffer));
                    GucStack *previous_stack = existing->stack;
                    int original_flags = existing->flags;
                    volatile int result = 0;
                    ErrorContextCallback error_context;
                    error_context.callback = ankus_guc_worker_restore_error_context;
                    error_context.arg = definition;
                    error_context.previous = error_context_stack;
                    ANKUS_PG_TRY_INNER();
                    {
                        existing->flags |= GUC_ALLOW_IN_PARALLEL;
                        error_context_stack = &error_context;
        #if PG_VERSION_NUM >= 150000
                        result = set_config_option_ext(definition->name, value,
                            existing->scontext, existing->source, existing->srole,
                            GUC_ACTION_SET, true, ERROR, true);
        #else
                        result = set_config_option(definition->name, value,
                            existing->scontext, existing->source, GUC_ACTION_SET, true, ERROR, true);
        #endif
                    }
                    ANKUS_PG_FINALLY_INNER();
                    {
                        error_context_stack = error_context.previous;
                        existing->flags = original_flags;
                    }
                    ANKUS_PG_END_TRY_INNER();
                    if (result <= 0)
                        ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                            errmsg("Ankus configuration could not be restored in a parallel worker")));
                    ankus_guc_retain_worker_baseline(existing, previous_stack);
                    definition->extra = existing->extra;
                    definition->worker_restore_pending = false;
                }
            }
            PG_FINALLY();
            {
                ankus_guc_replaying_worker_restore = false;
            }
            PG_END_TRY();
        }
        """;
}
