using Ankus.Examples.CustomSql;

// Bootstrap SQL runs before every generated schema, type and function. An extension has at most one bootstrap block.
[assembly: PgSql("bootstrap_raw", """
    CREATE TABLE extension_sql (message TEXT);
    INSERT INTO extension_sql VALUES ('bootstrap');
    """, Order = PgSqlOrder.Bootstrap)]

// A managed type reference orders this block after the generated dogs schema.
[assembly: PgSql("single_raw", """
    INSERT INTO extension_sql VALUES ('single_raw');
    """)]
[assembly: PgRequires(typeof(Home.Dogs), DeclarationId = "single_raw")]

// String IDs name other SQL blocks, including SQL files; type references name generated declarations.
[assembly: PgSql("multiple_raw", """
    INSERT INTO extension_sql VALUES ('multiple_raw');
    """, Requires = ["single_raw", "single"])]
[assembly: PgRequires(typeof(Home.Dogs.Dog), DeclarationId = "multiple_raw")]
[assembly: PgRequires(typeof(Home.Ball), DeclarationId = "multiple_raw")]

// SQL files are AdditionalFiles inputs. Their text is read at build time and included in the installation script.
// Without an explicit name, a file block is named after the file: these are "single" and "multiple".
[assembly: PgSqlFile("sql/single.sql", Requires = ["single_raw"])]
[assembly: PgSqlFile("sql/multiple.sql", Requires = ["single_raw", "single", "multiple_raw"])]
[assembly: PgRequires(typeof(Home.Dogs.Dog), DeclarationId = "multiple")]
[assembly: PgRequires(typeof(Home.Ball), DeclarationId = "multiple")]

// Final SQL runs after every generated and custom declaration. An extension has at most one final block.
[assembly: PgSqlFile("sql/finalizer.sql", Order = PgSqlOrder.Finalize)]
