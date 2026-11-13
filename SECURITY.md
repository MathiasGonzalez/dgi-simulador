# Seguridad

## Para qué es seguro

dgi-simulador es una herramienta de desarrollo y de CI. Está pensado para correr en la máquina de quien desarrolla o como servicio de un job, alcanzable solo desde ahí. No es un servicio para publicar en internet ni en una red compartida.

Con la configuración por defecto, quien llega al puerto puede:
- bajar las claves privadas de los certificados de prueba (`/admin/emisores/{rut}/certificado.pfx`);
- borrar todo el estado (`POST /admin/reset`);
- hacer que el simulador mande un pedido a la URL que indique (`urlServicio` del padrón y `url` de `/admin/intercambio/emitir`) y leer parte de la respuesta.

Por eso:
- fuera del contenedor escucha solo en `localhost`, y `compose.yaml` publica el puerto en `127.0.0.1`;
- si tiene que escuchar para otras máquinas, hay que definir `Simulador__ClaveAdmin` (README, sección Seguridad) y limitar quién llega al puerto;
- la CA de prueba y el certificado de "la DGI" no tienen que ser de confianza en ningún ambiente real.

Los datos que recibe son de prueba. No hay que mandarle certificados, CAE ni comprobantes reales.

## Versiones con correcciones

Las correcciones de seguridad salen para la última versión publicada (`1.x`).

## Avisar de un problema

Por favor no abras un issue público. Usá **Security → Report a vulnerability** de este repositorio en GitHub (aviso privado). Contá qué versión o tag de la imagen usás, cómo se reproduce y qué permite hacer.

Entran en el alcance los problemas que afectan a quien usa el simulador como está documentado: por ejemplo, que un mensaje SOAP sin firmar o un XML de un tercero permita leer archivos, ejecutar código o saltear la clave de administración. Lo que la API de administración permite hacer a quien tiene acceso a ella está descripto arriba y es parte del diseño.
