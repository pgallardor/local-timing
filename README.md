# RFID Local Race

Aplicación WinForms independiente del SDK RFID para preparar y operar una carrera autónoma sin Internet, Docker, Supabase ni Next.js. El capturador `RFIDTiming` sigue siendo otro proceso y envía al receptor local por HTTP de loopback.

## Compilar

Abrir `RFIDLocal.sln` con Visual Studio y compilar `Release | Any CPU`, o ejecutar desde esta carpeta:

```powershell
MSBuild RFIDLocal.sln /p:Configuration=Release /p:Platform="Any CPU"
```

Requiere el targeting pack de .NET Framework 4.8. La persistencia usa `winsqlite3.dll` que Windows 10/11 proporciona; no depende de un paquete NuGet ni necesita `Reader.dll`. El motor y los contratos de resultados compartidos están incluidos en `Shared/`, así que este repositorio puede compilarse por separado. La aplicación guarda datos en `%LOCALAPPDATA%\RFIDLocalRace\<event-id>\race.sqlite3`.

## Recorrido autónomo disponible

1. Crear evento local con fecha, zona horaria y catálogo de distancias. Queda en borrador.
2. Importar CSV/XLSX, revisar el preview y confirmar el lote. Se preserva el JSON de origen de cada fila, IDs estables por `participant_id` o dorsal y revisiones de nómina.
3. Configurar en `RFIDTiming` un perfil `local_receiver`, usando el endpoint mostrado, el UUID del evento/lector y la credencial de un solo uso.
4. Habilitar recepción local. El receptor escucha solo en `127.0.0.1`; el preflight informa estado y no requiere una clave Supabase.
5. Registrar salidas oficiales comunes por distancia o por categoría dentro de una distancia, consultar resultados/premios disponibles con filtros por dorsal y categoría, corregir marcas con motivo y marcar/quitar DNF. El motor de `Shared/` usa `compat_actual_v1`; cada snapshot se conserva ligado a las revisiones de nómina/marcas.
6. Crear respaldo `.yrbackup` desde el backup transaccional de SQLite. Restaurar produce una copia de consulta; hay que tomar autoridad local y emitir una credencial nueva antes de capturar.
7. Archivar un evento cerrado desde el menú «Evento» lo mueve a la carpeta local de archivados; «Restaurar evento archivado» lo devuelve a la lista activa sin alterar sus datos.

El receptor persiste payload, huella e idempotencia por lector/lectura, aplica la marca y el recibo en una transacción y responde después del commit. Los rechazos quedan visibles en Recibos; `unknown_chip` se puede reprocesar con el mismo ID y motivo auditado después de corregir la nómina. `event_closed` aún no se reprocesa porque falta definir la ventana temporal de conciliación; requiere corrección manual auditada. Al cerrar el evento, la app sigue respondiendo `event_closed` mientras el receptor está activo para que el outbox del lector los archive como bloqueados.

## Transferencia D07

1. En el evento web, fija su fecha si falta y descarga «Preparar operación local». La web reserva el evento y entrega un `.yrprep` con IDs/QR, distancias, nómina y política compatible; importa ese archivo en RFID Local Race.
2. Opera localmente. Para publicar, usa «Crear paquete» y entrega el `.yrtransfer` a la sección «Recibir revisión local» de la web. Previsualiza y confirma provisional o final; importa el `.yrreceipt` descargado para actualizar el estado local.
3. Un evento vinculado vuelve al mismo ID. Un evento autónomo crea un destino remoto nuevo; el vínculo a un evento existente con conciliación de participantes aún no está implementado.

## Límites de esta entrega

- El intercambio requiere mover los tres archivos manualmente; no hay sesión remota ni sincronización directa desde la app.
- No hay actualización/conciliación de preparación. Cambios remotos invalidan la huella base y detienen la publicación; la app no reemplaza ni mezcla su base local.
- Los recibos comprueban evento, revisión, ID de transferencia y hash, pero no llevan firma criptográfica del servidor.
- `chip` se incluye para permitir asociar lecturas RFID a corredores offline. RUT, credenciales y payload EPC de lecturas no se publican; evaluar si `chip` debe protegerse/seudonimizarse antes del piloto.
- La migración D07 y la función Edge deben desplegarse en Supabase antes de usar el puente.
- Solo existe el preset de resultados `compat_actual_v1`. La configuración de reglas/cupos de premiación por evento aún no está disponible; el motor muestra `NotConfigured` en esos campos.
- El importador acepta CSV y libros XLSX con encabezados consistentes entre hojas. Antes de cargar, propone y permite ajustar el vínculo entre encabezados y campos (por ejemplo `chips` → chip y `cat.` → categoría por edad); las columnas no asignadas se conservan en el origen de cada participante.
- La preparación no reemplaza la validación física del lector, la sincronización de reloj, los diagnósticos RF ni una prueba de recuperación en el equipo de carrera.
- No se ejecutaron pruebas integradas, medición de rendimiento, piloto con operador ni pruebas con hardware en esta entrega.

No copies `race.sqlite3` mientras la aplicación trabaja. Usa el paquete `.yrbackup`, que toma una copia consistente y valida su huella al restaurar. El outbox aún pendiente vive en `RFIDTiming` (`%LOCALAPPDATA%\RFIDTiming\Outbox`) y no forma parte del paquete de evento; respáldalo por separado antes de mover el puesto de captura. La credencial local no está dentro del paquete ni se guarda en texto legible; si se pierde, rótala desde la app y configura el lector con el nuevo valor.
