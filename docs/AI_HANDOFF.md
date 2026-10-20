# SqlSuite — Guía de continuidad para agentes

Este documento es el punto de partida para continuar el trabajo en SqlSuite sin asumir
comportamientos que no estén implementados. Describe la arquitectura, los flujos actuales, las
decisiones importantes y las limitaciones conocidas. **El código es la fuente de verdad**: si este
documento y la implementación difieren, confirma el comportamiento en el código y actualiza esta
guía.

## Estado actual

| Dato | Estado |
|---|---|
| Solución | .NET 8, `SqlSuiteApp.sln` |
| Aplicación | Blazor Server; comparación SQL ejecutada en el servidor |
| Proyectos | `SqlSuite.Core`, `SqlSuite.Web`, `SqlSuite.Core.Tests` |
| Rama publicada | `main` |
| Último hito conocido | `3063a34` — protecciones admin, claves DML por esquema y documentación |
| Verificación de ese hito | `dotnet build` correcto; 82 pruebas aprobadas |
| Documentación | README, este handoff, PRD y estructura del proyecto en `docs/` |

Al retomar, ejecuta `git status -sb` y `git log -5 --oneline` primero. El repositorio puede haber
avanzado desde la fecha de este documento.

## Inicio rápido

Desde la raíz del repositorio:

```bash
dotnet build SqlSuiteApp.sln
dotnet test tests/SqlSuite.Core.Tests/SqlSuite.Core.Tests.csproj
dotnet run --project src/SqlSuite.Web
```

La interfaz local se abre en `http://localhost:5199`. Para comparar bases de datos hacen falta
dos instancias accesibles de SQL Server; no presupongas que hay contenedores o credenciales
disponibles en el entorno del siguiente agente.

Para habilitar acciones administrativas, configura `SQLSUITE_ADMIN_PASSWORD` en el entorno del
proceso servidor antes de iniciarlo. No escribas la clave en el código, en archivos de configuración
versionados, en pruebas con valores reales ni en commits. Véase la sección [Modo administrador](#modo-administrador).

## Arquitectura y rutas de código

```text
src/SqlSuite.Core/       Comparación DDL/DML, modelos, generación y ejecución de scripts
src/SqlSuite.Web/        Blazor Server, UI y estado de cada circuito
tests/SqlSuite.Core.Tests/
                         Pruebas unitarias xUnit del núcleo
docs/                    PRD, estructura de referencia y esta guía
```

| Responsabilidad | Código principal |
|---|---|
| Arranque e inyección de dependencias | `src/SqlSuite.Web/Program.cs` |
| Shell de interfaz y modal de administrador | `src/SqlSuite.Web/Components/Pages/Home.razor` |
| Estado, selección, flujo de comparación y autorización de acciones | `src/SqlSuite.Web/ViewModels/ComparisonState.cs` |
| Modelo y comparación DDL | `src/SqlSuite.Core/Services/SchemaComparerService.cs`, `SchemaComparisonSession.cs` |
| Filtrado del script DDL por selección | `src/SqlSuite.Core/Services/ScriptObjectExtractor.cs` |
| Descubrimiento de claves y columnas | `src/SqlSuite.Core/Services/DacModelReader.cs` |
| Comparación fila a fila DML | `src/SqlSuite.Core/Services/DataComparerService.cs` |
| Generación del script DML | `src/SqlSuite.Core/Services/DataScriptGenerator.cs` |
| Ejecución del script por lotes | `src/SqlSuite.Core/Services/ScriptExecutionService.cs` |
| Validación de la clave admin y bloqueo de intentos | `src/SqlSuite.Web/Services/AdminAccessService.cs` |
| Vista de árbol DDL, tabla DML y panel de script | `src/SqlSuite.Web/Components/Shared/` |

`ComparisonState` es scoped: cada circuito SignalR/navegador conserva sus conexiones, selección,
resultados y script. `AdminAccessService` es singleton en el proceso y comparte el límite de
intentos fallidos entre circuitos.

## Flujos funcionales

### DDL — comparación de esquema

1. `SchemaComparerService` carga los modelos SQL Server con DacFx, compara origen y destino y
   obtiene diferencias y el script de despliegue.
2. `SchemaComparisonSession` mantiene vivo el resultado DacFx para poder cambiar selecciones sin
   volver a consultar ambas bases.
3. La selección es explícita: no se marca ningún objeto por defecto. Los hijos siguen la selección
   del objeto raíz.
4. El script DacFx completo se genera una vez y se cachea. Para aplicar la selección, se quitan del
   script los bloques de objetos excluidos usando `ScriptObjectExtractor`; no se usa
   `SchemaComparisonResult.Exclude`.
5. Los objetos ausentes en el destino solo se eliminan si el administrador habilitó la opción y se
   generó la comparación con esa opción activa.

La vista textual DDL compara definiciones anteriores del destino con las nuevas del origen cuando
DacFx puede exponer ambas definiciones; no equivale a mostrar el script completo de despliegue.

### DML — comparación de filas

1. `DacModelReader` obtiene columnas y una clave utilizable por tabla: PK, si no una restricción
   única o índice único no filtrado. Las claves se asocian por `esquema.tabla`, no solo por nombre.
2. Solo se comparan tablas presentes en ambos lados cuya clave y columnas permitan una comparación
   segura. Si no hay clave utilizable o las columnas de clave difieren entre origen y destino, la
   tabla se omite y se informa; no se adivina una identidad de fila.
3. `DataComparerService` lee las filas ordenadas por la clave y hace un merge; guarda los cambios
   encontrados, no la tabla completa.
4. Las filas comparables aparecen agrupadas por tabla y ninguna se selecciona inicialmente. El
   usuario selecciona filas o una tabla; la búsqueda solo filtra lo visible.
5. `DataScriptGenerator` produce DML solo con los cambios seleccionados. En inserciones con
   `IDENTITY`, emite `SET IDENTITY_INSERT ... ON/OFF` cuando la opción está activa.

Interpretación de cambios:

| Cambio de datos | Resultado DML |
|---|---|
| Cambia una columna regular, incluida una columna FK | `UPDATE` |
| Cambia el valor de una PK | La fila antigua se ve como `DELETE` y la nueva como `INSERT` |
| Cambia el valor de una clave FK | Es un `UPDATE`; la FK no es la identidad de la fila |
| Cambia la definición de una PK o FK | Es cambio de esquema y corresponde a DDL |

El comparador DML **no descubre ni ordena dependencias FK entre tablas**. Un script puede fallar
por restricciones referenciales aunque sus filas se hayan clasificado correctamente. Lee
[Limitaciones](../README.md#limitaciones-conocidas) antes de cambiar el generador.

### Aplicación de scripts

`ScriptExecutionService` normaliza variables de sqlcmd, divide el T-SQL por `GO` y ejecuta los
lotes secuencialmente en la base destino. La interfaz pide confirmación antes de aplicar. La
autorización administrativa se comprueba tanto al solicitar como al confirmar la aplicación;
no confíes en ocultar un botón como único control.

## Modo administrador: alcance y cautelas

- La app inicia en modo normal. El usuario pulsa **Activar administrador** e ingresa la clave.
- El servidor lee `SQLSUITE_ADMIN_PASSWORD` al inicializar `AdminAccessService`; cambiar la variable
  requiere reiniciar el proceso.
- Se conserva un hash SHA-256, la comparación de hashes es de tiempo constante y cinco fallos
  bloquean nuevos intentos durante un minuto. El contador es global al proceso.
- `CanApplyScript`, la solicitud y la confirmación de aplicar exigen modo admin. La opción de
  eliminar objetos ausentes también está bloqueada sin admin.
- Al salir del modo admin se desactiva la opción de drops y se limpian resultados.
- **Esto no es autenticación de usuarios ni un sistema de roles.** Cualquier persona que pueda
  acceder a la aplicación y conozca la clave compartida puede elevar su sesión. Mantén el servidor
  en localhost o detrás de controles de acceso de confianza; no lo expongas a una red pública.

Nunca documentes aquí la clave activa ni copies valores de conexión reales. Usa marcadores de
posición en ejemplos y variables de entorno para secretos.

## Decisiones que conviene preservar

1. **Selección segura por defecto:** ningún objeto DDL ni fila DML queda seleccionado después de
   comparar; el script refleja solo selecciones explícitas.
2. **Identidad DML basada en clave real:** PK primero, luego clave única utilizable; nunca asumir
   que una FK identifica una fila.
3. **No comparar ambiguamente:** las claves de origen/destino deben coincidir; si no, se omite la
   tabla.
4. **DDL con script cacheado:** DacFx `Exclude` se evitó por comportamiento no reversible observado
   con la versión del proyecto; el filtrado se hace sobre bloques del script generado.
5. **Separación de responsabilidades:** lógica SQL en `Core`; estado/UI en `Web`; pruebas unitarias
   enfocadas en el núcleo.
6. **No tratar el modo admin como seguridad de red:** no sustituye autenticación real.

## Limitaciones conocidas

- DML no ordena operaciones entre tablas según FK; pueden requerirse ajustes manuales.
- Las tablas sin PK/índice/constraint único utilizable se omiten en DML.
- La web no tiene autenticación de usuario. Admin es un control de acciones compartido, no una
  frontera de identidad.
- Los flujos de integración contra bases SQL Server requieren instancias externas y no quedan
  cubiertos por las pruebas unitarias normales.
- Al modificar el filtrado DDL, prueba que seleccionar, deseleccionar y volver a seleccionar
  objetos produce un script correcto, sin incluir bloques no seleccionados.

Consulta el README para instrucciones de uso detalladas y el PRD para requisitos originales. No
interpretes documentos de referencia antiguos como prueba de que una función ya está implementada:
verifica su código y pruebas.

## Checklist para retomar una tarea

- [ ] Leer `git status -sb`, la rama y los últimos commits.
- [ ] Leer este handoff y el fragmento pertinente del README/PRD.
- [ ] Inspeccionar las implementaciones y pruebas actuales antes de proponer comportamiento.
- [ ] No reutilizar ni divulgar secretos del entorno o del historial.
- [ ] Hacer un cambio acotado y añadir o actualizar pruebas.
- [ ] Ejecutar build y las pruebas pertinentes; informar explícitamente qué no pudo validarse.
- [ ] Actualizar esta guía y el README si cambian flujos, decisiones o limitaciones.
