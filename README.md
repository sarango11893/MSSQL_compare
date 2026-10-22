# SQL Suite

Aplicación local para comparar y sincronizar bases de datos **Microsoft SQL Server**:
estructura (DDL) y datos (DML). Diseñada como una alternativa independiente y multiplataforma 
a las soluciones de pago tradicionales (schema and data compare).
Se ejecuta en macOS, Linux y Windows; la interfaz se abre en el navegador.

- **.NET 8** · **Blazor Server** para la interfaz (se ejecuta en macOS, Linux y Windows)
- **Microsoft.SqlServer.DacFx** para la comparación de esquemas y la lectura de metadatos
- **Microsoft.Data.SqlClient** para la comparación de datos en streaming

El documento de requisitos y la estructura de referencia están en [docs/PRD.txt](docs/PRD.txt)
y [docs/Estructura_Proyecto.txt](docs/Estructura_Proyecto.txt). Para retomar trabajo técnico con
otro agente, consulta la [guía de continuidad](docs/AI_HANDOFF.md), que resume arquitectura,
flujos actuales, decisiones y limitaciones conocidas.

---

## Estructura de la solución

```
SqlSuiteApp.sln
├── src/SqlSuite.Core             Lógica de negocio (net8.0, sin dependencia de UI)
├── src/SqlSuite.Web              Interfaz Blazor Server (net8.0, multiplataforma)
└── tests/SqlSuite.Core.Tests     Pruebas unitarias (xUnit)
```

### Interfaz Blazor Server

La interfaz vive en `src/SqlSuite.Web` y sustituye al proyecto WPF original (que solo se podía
ejecutar en Windows). DacFx y `Microsoft.Data.SqlClient` se ejecutan **en el servidor** —el
navegador solo recibe HTML y eventos por SignalR—, así que la app funciona igual en macOS y
Linux, y cualquier navegador moderno sirve como cliente.

La organización mantiene la separación de responsabilidades de MVVM sin arrastrar
`ICommand`/`INotifyPropertyChanged`:

| Carpeta | Contenido |
|---|---|
| `ViewModels/ConnectionState.cs` | Estado de un panel de conexión: probar, listar bases de datos |
| `ViewModels/ComparisonState.cs` | Orquestación: modo, opciones, comparar, aplicar, mensajes |
| `Components/Pages/Home.razor` | Shell principal (selector de modo, paneles, panel de script) |
| `Components/Shared/` | `ConnectionPanel`, `SchemaDiffTree`, `DataDiffTable`, `ScriptPanel` |
| `wwwroot/` | `app.css` (paleta y layout) y `js/clipboard.js` (copiar al portapapeles) |

El estado se registra como `AddScoped`, de modo que cada pestaña del navegador tiene su propia
sesión de comparación. Los avisos de despliegue y la confirmación de aplicación usan el mismo
lenguaje visual que el resto de la interfaz en lugar de `MessageBox`.

### Módulo A — Schema Compare (DDL)

`SchemaComparerService` carga dos modelos con `TSqlModel.LoadFromDatabase`, los compara con
`SchemaComparison` (envolviendo cada conexión en un `SchemaCompareDatabaseEndpoint`) y pide a
DacFx el script de despliegue con `GenerateScript(databaseName)`.

El resultado se proyecta a un árbol de `SchemaDifferenceInfo` (`Create` / `Alter` / `Drop`),
donde las diferencias de tipo «propiedad» de DacFx se cuentan pero no se muestran como nodos,
para que el árbol refleje objetos y no ruido interno.

Después de una comparación de esquema, pulsa el nombre de un objeto en **Comparison Visualizer**
para abrir directamente su diff de texto, cuando DacFx expone su definición. La vista compara
el texto actual del destino con el texto deseado del origen y resalta líneas eliminadas y
añadidas; no usa el script global de despliegue como sustituto de las definiciones individuales.
En la vista de árbol, los grupos de objetos empiezan colapsados. El panel del script permite copiar
su contenido o descargarlo como archivo `.sql`.
Los grupos DML por tabla también comienzan colapsados. Pulsa la clave de un registro para abrir
la comparación textual de sus valores anteriores (destino) y nuevos (origen), resaltando los
valores eliminados y añadidos.

### Modo administrador

La aplicación inicia siempre en modo normal. Para habilitar «Aplicar en destino» y la opción de
eliminar objetos ausentes, configura `SQLSUITE_ADMIN_PASSWORD` en el entorno del proceso servidor
y reinicia la aplicación. Por ejemplo, en macOS/Linux define la variable en la misma terminal
desde la que iniciarás el servidor:

```bash
export SQLSUITE_ADMIN_PASSWORD="elige-una-clave-segura"
dotnet run --project src/SqlSuite.Web
```

El usuario puede activar el modo administrador desde la barra superior, introduciendo esa clave;
la validación y el límite global de cinco intentos por minuto se aplican en el servidor. Al salir
del modo se desactivan las eliminaciones y se limpian los resultados. Si la variable no está
configurada, esas operaciones permanecen bloqueadas. Este modo protege las acciones de la interfaz,
pero **no sustituye la autenticación de usuarios**: no expongas la aplicación a una red no confiable.

> **La selección no usa `SchemaComparisonResult.Exclude`.** Ese API resulta poco fiable en DacFx
> 162.x: devuelve `false` y deja `Included == true` cuando el nodo tiene dependencias de
> exclusión, `GetExcludeDependencies` lanza `NotSupportedException`/`ArgumentException` al
> enumerar, y el estado **no es reversible** (`Include` devuelve `True` pero el script nunca
> vuelve a crecer). Por eso `SchemaComparisonSession` genera el script completo **una sola vez**,
> lo cachea y en cada cambio de selección recorta los bloques de los objetos excluidos con
> `ScriptObjectExtractor.Remove` (0 ms en lugar de los ~4,4 s de una regeneración).

### Módulo B — Data Compare (DML)

`DacModelReader` extrae de DacFx las tablas con una clave utilizable (PK → índice único →
restricción única) y sus columnas escribibles. `DataComparerService` abre un `SqlDataReader`
por lado y hace un *merge join* ordenado por clave: nunca carga una tabla completa en memoria.
Cada fila se clasifica como `Insert`, `Update` o `Delete`, y `DataScriptGenerator` produce un
script envuelto en transacción (borrados → actualizaciones → inserciones) usando
`SqlLiteralFormatter` para los literales T-SQL.
La clave de fila se obtiene de la PK (o de la alternativa única) y debe coincidir en origen y
destino. Un cambio en una columna normal, incluida una FK, se detecta como `Update`; cambiar el
valor de la PK cambia la identidad de la fila y se representa como `Delete` + `Insert`.
Las FK no se usan para identificar filas ni para ordenar las operaciones entre tablas.

Se omiten (y se reportan) las tablas sin clave, las que no existen en destino y las excluidas
por configuración. `IsEqual` solo es `true` cuando **todas** las tablas se pudieron comparar
y ninguna presentó diferencias; una tabla omitida no se considera una coincidencia probada.

---

## Requisitos

- **.NET 8 SDK** (único requisito para compilar y ejecutar)
- Acceso a dos instancias de SQL Server (origen y destino). En macOS/Linux lo más simple es
  Docker:

  ```bash
  docker run -d --name sqlsuite-src -p 14331:1433 \
    -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD -e MSSQL_PID=Developer \
    mcr.microsoft.com/mssql/server:2022-latest

  docker run -d --name sqlsuite-tgt -p 14332:1433 \
    -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD -e MSSQL_PID=Developer \
    mcr.microsoft.com/mssql/server:2022-latest
  ```

  Define `MSSQL_SA_PASSWORD` en el entorno antes de ejecutar los comandos; SQL Server exige
  una contraseña que cumpla sus requisitos de complejidad. No guardes credenciales reales en el
  repositorio.

  Desde el equipo anfitrión las conexiones son `localhost,14331` y `localhost,14332`.
  En la interfaz se usan esas direcciones con el usuario `sa` y la contraseña configurada en el
  entorno; la aplicación web **no tiene login propio**.

## Compilar y probar

```bash
dotnet restore SqlSuiteApp.sln
dotnet build   SqlSuiteApp.sln
dotnet test    tests/SqlSuite.Core.Tests/SqlSuite.Core.Tests.csproj
```

## Ejecutar la interfaz

```bash
dotnet run --project src/SqlSuite.Web/SqlSuite.Web.csproj
```

Abre **<http://localhost:5199>** en el navegador. El puerto se puede cambiar con
`--urls` (`dotnet run --project src/SqlSuite.Web --urls http://localhost:8080`) y la
aplicación escucha solo en `localhost` por defecto.

### Publicar

```bash
dotnet publish src/SqlSuite.Web/SqlSuite.Web.csproj -c Release -o publish/web
dotnet publish/web/SqlSuite.Web.dll --urls http://localhost:5199
# o simplemente:
dotnet run --project src/SqlSuite.Web -c Release
```

El resultado es portable: el mismo `publish/web` corre en macOS, Linux y Windows siempre que
exista el runtime **ASP.NET Core 8**. Si prefieres un paquete autocontenido, añade
`-r osx-arm64 --self-contained true` (o el RID correspondiente).

## Uso

1. Configura **Origen** y **Destino** desde **Conexiones** (servidor, base de datos,
   autenticación integrada o SQL). *Probar* valida la conexión y *Bases de datos* lista los
   catálogos disponibles.
2. Elige el modo **Esquema (DDL)** o **Datos (DML)** en el selector superior.
3. Ajusta las **Opciones de prueba**: eliminar objetos que no están en el origen,
   bloquear si hay posible pérdida de datos, script dentro de transacción, comparación sensible
   a mayúsculas y máximo de diferencias guardadas por tabla.
4. **Ejecutar comparación** muestra las diferencias. Por seguridad, no se selecciona ningún
   objeto ni registro automáticamente y el script queda vacío.
5. Marca explícitamente los objetos o registros que quieres sincronizar. La búsqueda filtra los
   resultados sin cambiar la selección.
6. Abre **Generated T-SQL Sync Script** para revisar el script final. **Copiar** copia el
   contenido; **Aplicar** solo se habilita si hay un script generado, pide confirmación y lo
   ejecuta contra la base de datos de destino. Después vuelve a comparar automáticamente.

### El árbol de diferencias

En modo DDL, las diferencias se pueden ver como matriz o como árbol agrupado por tipo de objeto
(`Tablas`, `Vistas`, `Procedimientos`, `Funciones`, `Sinónimos`, `Secuencias`, `Tipos de tabla`,
`Tipos de datos`…). Cada grupo del árbol es **colapsable** y muestra cuántos objetos `CREAR` /
`CAMBIAR` / `ELIMINAR` contiene.

* Todos los objetos aparecen **sin marcar** después de comparar. Marca cada objeto que quieres
  incluir; el checkbox de grupo selecciona o deselecciona de golpe los objetos visibles.
* La búsqueda por nombre o tipo solo filtra la vista, no cambia qué objetos están marcados.
* La barra muestra `N de M objetos incluidos`; **Seleccionar todo** es una acción explícita.
  El script final se regenera con la selección actual.
* Excluir un objeto padre excluye también sus objetos hijos (`Columnas`, `Índices`, `Triggers`,
  `CheckConstraints`, `ForeignKeys`…), los bloques de objetos no seleccionados y sus guardas de
  pérdida de datos. Se conservan la transacción y las validaciones de constraints que correspondan
  a los objetos seleccionados.
* Los objetos marcados como `ELIMINAR` solo producen `DROP` si activas **Eliminar objetos ausentes
  en el origen** y vuelves a comparar. Esa opción es conservadora y está desactivada por defecto;
  si intentas incluir eliminaciones sin activarla, la interfaz lo indica.

### Diferencias de datos (DML)

La comparación exacta recorre las filas comparables de ambos lados en orden de clave; en bases
grandes puede tardar varios minutos. Durante el proceso, el estado indica la etapa, la tabla actual
y el número de filas leídas, y **Cancelar** interrumpe las lecturas SQL. **Máximo de diferencias
guardadas por tabla** limita el resultado acumulado, no las filas que se deben leer para comparar;
si se alcanza, el resultado se marca incompleto y no genera un script parcial.

Cuando ambos catálogos están en el mismo servidor, se usa una comparación `SELECT` entre bases
ejecutada en SQL Server para los tipos de datos compatibles. Esto evita transferir a la aplicación
las filas que coinciden. Requiere que la cuenta tenga permiso de lectura en ambas bases desde la
misma conexión. Tipos no compatibles o comparación de texto sin distinción de mayúsculas usan el
comparador exacto existente como alternativa; no se omiten diferencias por muestreo.

La lista editable de tablas DML está en `src/SqlSuite.Web/appsettings.json`, bajo
`DataComparison:IncludedTables`. Se compara solo esa lista; se aceptan nombres de tabla sin esquema
(coinciden en cualquier esquema) o `esquema.tabla`. Al ejecutar una publicación desplegada, edita
el `appsettings.json` situado junto a la aplicación. Los nombres repetidos no son necesarios. Si
la lista queda vacía, se comparan todas las tablas.

* Los cambios se agrupan por tabla. El checkbox de la cabecera de cada tabla incluye o excluye
  todos sus registros; los checkboxes de fila permiten elegir registros concretos, incluso
  cambios de tipo `INSERT`, `UPDATE` o `DELETE`.
* Al expandir una tabla, la cuadrícula muestra los nombres reales de todas las columnas y una fila
  por cada registro diferente. La fila y los encabezados de columnas con cambios se resaltan en rojo;
  las celdas diferentes usan un resaltado más marcado. Al pulsarlas se abre una vista
  previa acotada con los valores de destino y origen. La cuadrícula tiene desplazamiento propio
  para no alargar toda la página.
* La opción **Tratar CRLF y LF como iguales** está activa por defecto y evita diferencias causadas
  únicamente por el formato del salto de línea. Al desactivarla, los finales de línea se comparan
  estrictamente. El resto del contenido, incluidos espacios y mayúsculas según la opción elegida,
  sigue comparándose.
* Ningún registro está seleccionado por defecto. **Seleccionar todos** afecta a todos los cambios
  comparables; la búsqueda puede localizar por nombre de tabla, clave, columna o valor y no altera
  la selección.
* El contador `N de M registros incluidos` y el script final se actualizan conforme seleccionas
  filas. Las tablas omitidas no se pueden seleccionar.
* Para filas nuevas en tablas con `IDENTITY`, el script conserva el valor de la clave de origen y
  emite `SET IDENTITY_INSERT [esquema].[tabla] ON` antes de los `INSERT` y `OFF` después. Así los
  identificadores quedan iguales en ambos lados; si no hay inserciones, esas cláusulas no aparecen.

### Ver el script y copiar

* **Buscar objeto…** filtra DDL por nombre o tipo. En DML puedes buscar por tabla, clave, columna
  o valor. La búsqueda solo afecta a la vista: no cambia la selección; **Limpiar** la desactiva.
* Al pulsar el **nombre** de un objeto, el panel de script muestra únicamente los bloques de ese
  objeto (con el preámbulo `SET …` / `USE [..]` que el script necesita para ejecutarse) y la fila
  queda resaltada. El extracto se obtiene recortando el script ya generado, no volviendo a
  invocar a DacFx, así que es instantáneo.
* En **Comparar texto**, los paneles de destino (anterior) y origen (nuevo) se distinguen
  visualmente, las líneas están alineadas con sus números y las líneas largas se ajustan al ancho
  disponible. Para una diferencia DML también se muestran los valores originales y sus tipos;
  los espacios al inicio o al final, tabulaciones y saltos de línea se hacen visibles. Si el
  comparador detectó cambios pero el diff textual no, la vista lo indica explícitamente.
* El conmutador **Ver script final / Ver script del objeto** alterna entre el script completo de
  la selección actual y el del objeto enfocado.
* **Copiar** copia al portapapeles lo que el panel está mostrando: el script final o el extracto
  del objeto.

> **`BlockOnPossibleDataLoss` está activado por defecto**: DacFx abortará el despliegue si un
> cambio puede perder datos (por ejemplo, quitar una columna con filas). Desactívalo de forma
> consciente o resuelve antes la pérdida de datos.
>
> **`DropObjectsNotInSource` está desactivado por defecto**: el script solo crea y modifica.
> Si quieres que el destino quede idéntico al origen hay que marcarlo; de lo contrario la
> comparación seguirá mostrando los objetos sobrantes como «a eliminar». Después de cambiar la
> opción hay que volver a comparar para que DacFx incluya los `DROP` en el script.

---

## Desviaciones respecto del código de plantilla

El archivo `Core_Services_Base.txt` original no compila contra DacFx 162.x. Estas son las
correcciones aplicadas, todas verificadas contra la API real por reflexión y en ejecución:

| Plantilla | Realidad en DacFx 162.5.57 |
|---|---|
| `DacProfile.GetProfile(connectionString)` | No existe. El modelo se carga con `TSqlModel.LoadFromDatabase(...)`. |
| `new SchemaComparison(sourceCs, targetCs)` | No existe. Se usan dos endpoints: `new SchemaComparison(new SchemaCompareDatabaseEndpoint(cs1), new SchemaCompareDatabaseEndpoint(cs2))`. |
| `comparison.Compare()` devuelve el script | Devuelve un `SchemaComparisonResult`; el script se pide aparte con `GenerateScript(databaseName)`. |
| `Column.Type` | No existe; se usa `Column.DataType` (un `TSqlObject`). |
| `Column.DefaultConstraint` | No existe; los defaults son objetos `DefaultConstraint` con `Host` / `TargetColumn`. |
| Columnas calculadas | Tienen `DataType` vacío y `Expression` con la fórmula; hay que detectarlas por `Expression`. |
| `Index.IsPrimaryKey` / `IsUniqueConstraint` | No existen. El tipo de clave se deduce de `PrimaryKeyConstraint` / `UniqueConstraint` / `Index`. |
| Script generado ejecutable directamente | DacFx emite **sqlcmd**: `:setvar`, `GO`, `$(DatabaseName)` y un guardián que ejecuta `SET NOEXEC ON` fuera de sqlcmd. |

### El script de DacFx y sqlcmd

`schema.sql` tal cual lo entrega DacFx **no se puede ejecutar** con `SqlCommand`: falla en los
`GO` y, si se trocea, el guardián `SET NOEXEC ON` desactiva el lote entero en silencio (el
despliegue «funciona» pero no cambia nada). `SqlCmdScriptProcessor` resuelve el problema:

- sustituye `$(Variable)` por el valor declarado en `:setvar` (o por el nombre de la base);
- elimina `:setvar`, `:on error exit`, `:r`, `:include` y el bloque guardián `__IsSqlCmdEnabled`;
- conserva el resto del script y lo trocea en lotes por `GO` (respetando `GO n`);
- reporta como advertencia cualquier variable que no se haya podido resolver.

`ScriptExecutionService` usa ese procesador y ejecuta los lotes en orden sobre una sola
conexión, deteniéndose en el primer error e informando el número de lote, su texto y el mensaje
de SQL Server.

### Comparación de datos

- Se usa `CommandBehavior.Default` (no `SequentialAccess`, que rompe las lecturas por nombre).
- La comparación de `string` es ordinal (sensible a mayúsculas) salvo que se desactive
  `CaseSensitiveDataComparison`.
- `binary`, `varbinary`, `image`, `rowversion` se comparan byte a byte; el resto por
  `IComparable` y, si los tipos difieren, por su representación textual invariante.
- Las columnas calculadas y `rowversion` se leen pero nunca se escriben.

---

## Limitaciones conocidas

- El script DML ordena por tabla (borrados → actualizaciones → inserciones) pero **no conoce las
  claves foráneas** ni calcula dependencias entre tablas, así que en esquemas con FK puede requerir
  orden manual o deshabilitar temporalmente las restricciones.
- No se comparan objetos de seguridad, permisos ni usuarios.
- La interfaz web **no tiene autenticación**: escucha en `localhost` y cualquiera que pueda
  abrirla puede conectarse con las credenciales que escriba. No la expongas en una red no
  confiable (usa un túnel SSH o un proxy con autenticación si necesitas acceso remoto).
- Las credenciales de SQL viven en la memoria del servidor durante la sesión del navegador;
  no se escriben en disco ni en el script generado.
