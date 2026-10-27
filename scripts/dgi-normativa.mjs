#!/usr/bin/env node
// Detecta cambios en las fuentes normativas de DGI: compara el texto normalizado
// de cada fuente de .github/dgi-normativa/fuentes.json con su snapshot commiteado.
//
// Uso:
//   node scripts/dgi-normativa.mjs                     informa cambios (no escribe snapshots)
//   node scripts/dgi-normativa.mjs --informe out.md    además escribe el informe en Markdown
//   node scripts/dgi-normativa.mjs --actualizar        reescribe los snapshots con lo publicado hoy
//
// En GitHub Actions escribe `cambios=true|false` en $GITHUB_OUTPUT.
import { readFile, writeFile, mkdir, appendFile } from "node:fs/promises";
import { createHash } from "node:crypto";

const DIR = new URL("../.github/dgi-normativa/", import.meta.url);
const SNAPSHOTS = new URL("snapshots/", DIR);
const TIMEOUT_MS = 30_000;
const MAX_LINEAS_DIFF = 150;

const args = process.argv.slice(2);
const actualizar = args.includes("--actualizar");
const informeIdx = args.indexOf("--informe");
const informePath = informeIdx >= 0 ? args[informeIdx + 1] : null;

const fuentes = JSON.parse(await readFile(new URL("fuentes.json", DIR), "utf8"));

const ENTIDADES = {
  nbsp: " ",
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  ordm: "º",
  ordf: "ª",
  deg: "°",
  ndash: "–",
  mdash: "—",
  laquo: "«",
  raquo: "»",
  ldquo: "“",
  rdquo: "”",
  lsquo: "‘",
  rsquo: "’",
  hellip: "…",
};
for (const [letra, marcas] of Object.entries({ a: "acute", e: "acute", i: "acute", o: "acute", u: "acute|uml", n: "tilde" }))
  for (const marca of marcas.split("|"))
    for (const l of [letra, letra.toUpperCase()]) ENTIDADES[l + marca] = (l + { acute: "\u0301", uml: "\u0308", tilde: "\u0303" }[marca]).normalize("NFC");

function decodificar(s) {
  return s.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e) => {
    if (e[0] === "#") {
      const n = e[1] === "x" || e[1] === "X" ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return Number.isFinite(n) ? String.fromCodePoint(n) : m;
    }
    return ENTIDADES[e] ?? ENTIDADES[e.toLowerCase()] ?? m;
  });
}

function lineas(texto) {
  return texto
    .split("\n")
    .map((l) => l.replace(/\s+/g, " ").trim())
    .filter(Boolean);
}

// Texto visible más la lista de archivos enlazados (las versiones de los formatos
// cambian de nombre, así que un documento nuevo aparece como un enlace nuevo).
function normalizarHtml(html, base, patronEnlaces) {
  const enlaces = new Set();
  if (patronEnlaces) {
    const re = new RegExp(patronEnlaces, "i");
    for (const [, href] of html.matchAll(/href\s*=\s*["']([^"'#]+)["']/gi)) {
      try {
        const url = new URL(decodificar(href), base).href;
        if (re.test(url)) enlaces.add(url);
      } catch {}
    }
  }
  const texto = decodificar(
    html
      .replace(/<(script|style|noscript|svg)[\s\S]*?<\/\1>/gi, " ")
      .replace(/<!--[\s\S]*?-->/g, " ")
      .replace(/<br\s*\/?>|<\/(p|div|li|tr|h\d|td|th|section|article)>/gi, "\n")
      .replace(/<[^>]+>/g, " ")
  );
  const salida = lineas(texto);
  if (enlaces.size) salida.push("", "## Enlaces", ...[...enlaces].sort());
  return salida.join("\n");
}

async function leer(fuente) {
  const res = await fetch(fuente.url, {
    signal: AbortSignal.timeout(TIMEOUT_MS),
    headers: { "user-agent": "dgi-simulador-normativa (+https://github.com/MathiasGonzalez/dgi-simulador)" },
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  if (fuente.tipo === "binario") {
    const buf = Buffer.from(await res.arrayBuffer());
    return `sha256 ${createHash("sha256").update(buf).digest("hex")}`;
  }
  // IMPO publica en ISO-8859-1: se respeta el charset del header o del <meta>.
  const bytes = Buffer.from(await res.arrayBuffer());
  const charset =
    /charset=["']?([\w-]+)/i.exec(res.headers.get("content-type") ?? "")?.[1] ??
    /<meta[^>]+charset=["']?([\w-]+)/i.exec(bytes.subarray(0, 4096).toString("latin1"))?.[1] ??
    "utf-8";
  const cuerpo = new TextDecoder(charset).decode(bytes);
  if (fuente.tipo === "html") return normalizarHtml(cuerpo, fuente.url, fuente.enlaces);
  return lineas(cuerpo).join("\n");
}

// Diff por líneas (LCS) con contexto: los estados "Publicado"/"Sustituido" se repiten,
// así que hace falta la posición y no solo el contenido.
function diff(antes, despues, contexto = 2) {
  const a = antes.split("\n");
  const b = despues.split("\n");
  const lcs = Array.from({ length: a.length + 1 }, () => new Uint32Array(b.length + 1));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const ops = [];
  let i = 0;
  let j = 0;
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) {
      ops.push(["  ", a[i]]);
      i++;
      j++;
    } else if (j < b.length && (i === a.length || lcs[i][j + 1] >= lcs[i + 1][j])) ops.push(["+ ", b[j++]]);
    else ops.push(["- ", a[i++]]);
  }
  const visibles = new Set();
  ops.forEach(([op], k) => {
    if (op !== "  ") for (let c = k - contexto; c <= k + contexto; c++) visibles.add(c);
  });
  const salida = [];
  let anterior = -1;
  ops.forEach(([op, linea], k) => {
    if (!visibles.has(k)) return;
    if (anterior >= 0 && k > anterior + 1) salida.push("@@");
    salida.push(op + linea);
    anterior = k;
  });
  const recorte = salida.length > MAX_LINEAS_DIFF ? [`… (${salida.length - MAX_LINEAS_DIFF} líneas más)`] : [];
  return [...salida.slice(0, MAX_LINEAS_DIFF), ...recorte].join("\n");
}

await mkdir(SNAPSHOTS, { recursive: true });

const cambios = [];
const errores = [];
for (const fuente of fuentes) {
  const archivo = new URL(`${fuente.id}.txt`, SNAPSHOTS);
  let actual;
  try {
    actual = await leer(fuente);
  } catch (e) {
    errores.push({ fuente, error: e.message ?? String(e) });
    console.warn(`✗ ${fuente.id}: ${e.message ?? e}`);
    continue;
  }
  const previo = await readFile(archivo, "utf8").catch(() => null);
  if (previo?.trimEnd() === actual) {
    console.log(`= ${fuente.id}`);
    continue;
  }
  console.log(`${previo === null ? "+" : "≠"} ${fuente.id}`);
  cambios.push({ fuente, nuevo: previo === null, diff: previo === null ? "" : diff(previo.trimEnd(), actual) });
  if (actualizar) await writeFile(archivo, actual + "\n");
}

if (errores.length === fuentes.length) {
  console.error("No se pudo leer ninguna fuente.");
  process.exit(1);
}

if (informePath) {
  const partes = [];
  for (const { fuente, nuevo, diff: d } of cambios) {
    partes.push(`### ${fuente.nombre}`, "", `Fuente: ${fuente.url}`, `Snapshot: \`.github/dgi-normativa/snapshots/${fuente.id}.txt\``, "");
    partes.push(nuevo ? "_Fuente nueva, sin snapshot previo._" : ["```diff", d, "```"].join("\n"), "");
  }
  if (errores.length) {
    partes.push("### No se pudieron leer", "", ...errores.map(({ fuente, error }) => `- ${fuente.nombre} (${fuente.url}): ${error}`), "");
  }
  await writeFile(informePath, partes.join("\n"));
}

if (process.env.GITHUB_OUTPUT) {
  await appendFile(process.env.GITHUB_OUTPUT, `cambios=${cambios.length > 0}\n`);
}
