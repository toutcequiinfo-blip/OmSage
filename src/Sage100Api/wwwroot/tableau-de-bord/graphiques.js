// Graphiques SVG sans bibliothèque externe (le serveur peut être sans Internet) : courbes, barres, barres horizontales, anneau, mini-courbe.
// Chaque fonction dessine dans un élément et se redessine quand sa largeur change.

const NS = "http://www.w3.org/2000/svg";
export const COULEURS = ["var(--c1)", "var(--c2)", "var(--c3)", "var(--c4)", "var(--c5)", "var(--c6)", "var(--c7)", "var(--c8)"];
const echapper = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

export const nombre = (n, d = 0) => (Number(n) || 0).toLocaleString("fr-FR", { maximumFractionDigits: d, minimumFractionDigits: d });
/** Montant complet pour les infobulles et légendes : 1 234 567,00. */
export const montant = (n) => nombre(n, 2);
/** 1 234 567 -> « 1,23 M » : graduations des axes (place comptée). */
export function compact(n) {
  const v = Number(n) || 0, a = Math.abs(v);
  if (a >= 1e9) return nombre(v / 1e9, a >= 1e10 ? 1 : 2) + " Md";
  if (a >= 1e6) return nombre(v / 1e6, a >= 1e7 ? 1 : 2) + " M";
  if (a >= 1e4) return nombre(v / 1e3, 0) + " k";
  return nombre(v, 0);
}

// ---------- Infobulle partagée ----------
const bulle = () => document.getElementById("infobulle");
export function montrerBulle(e, html) {
  const b = bulle();
  b.innerHTML = html;
  b.hidden = false;
  const x = Math.min(e.clientX + 14, window.innerWidth - b.offsetWidth - 8);
  const y = Math.min(e.clientY + 14, window.innerHeight - b.offsetHeight - 8);
  b.style.left = `${x}px`;
  b.style.top = `${y}px`;
}
export const cacherBulle = () => { bulle().hidden = true; };

// Redessin à la largeur du conteneur.
const observes = new WeakMap();
const observateur = new ResizeObserver((entrees) => {
  for (const e of entrees) {
    const o = observes.get(e.target);
    if (o && Math.abs(o.largeur - e.contentRect.width) > 4) { o.largeur = e.contentRect.width; o.dessiner(); }
  }
});
function suivre(el, dessiner) {
  observes.set(el, { largeur: el.clientWidth, dessiner });
  observateur.observe(el);
  dessiner();
}

function svg(largeur, hauteur) {
  const s = document.createElementNS(NS, "svg");
  s.setAttribute("viewBox", `0 0 ${largeur} ${hauteur}`);
  s.setAttribute("height", hauteur);
  return s;
}
function el(nom, attributs, parent) {
  const e = document.createElementNS(NS, nom);
  for (const [k, v] of Object.entries(attributs)) e.setAttribute(k, v);
  parent?.appendChild(e);
  return e;
}

function echelle(valeurs) {
  let min = Math.min(0, ...valeurs), max = Math.max(0, ...valeurs);
  if (min === max) max = min + 1;
  const pas = Math.pow(10, Math.floor(Math.log10((max - min) / 4)));
  const pasJoli = [1, 2, 2.5, 5, 10].map((m) => m * pas).find((p) => (max - min) / p <= 5) ?? pas * 10;
  min = Math.floor(min / pasJoli) * pasJoli;
  max = Math.ceil(max / pasJoli) * pasJoli;
  const graduations = [];
  for (let v = min; v <= max + pasJoli / 2; v += pasJoli) graduations.push(v);
  return { min, max, graduations };
}

function legende(conteneur, series) {
  const l = document.createElement("div");
  l.className = "legende";
  l.innerHTML = series.map((s, i) => `<span><i class="${s.pointille ? "pointille" : ""}" style="background:${s.couleur ?? COULEURS[i % 8]};border-color:${s.couleur ?? COULEURS[i % 8]}"></i>${echapper(s.nom)}</span>`).join("");
  conteneur.appendChild(l);
}

/**
 * Barres verticales (groupées ou empilées) avec courbes éventuelles par-dessus.
 * series : [{ nom, valeurs, couleur, type: "barre" | "ligne", pointille }]
 */
export function barres(conteneur, { categories, series, empile = false, hauteur = 220, format = montant, surClic }) {
  conteneur.innerHTML = "";
  if (!categories.length) { conteneur.innerHTML = '<p class="vide">Aucune donnée.</p>'; return; }
  legende(conteneur, series);
  const zone = document.createElement("div");
  zone.className = "graphe";
  conteneur.appendChild(zone);
  suivre(zone, () => {
    zone.innerHTML = "";
    const L = Math.max(zone.clientWidth, 260), H = hauteur, g = 46, d = 8, h = 8, b = 22;
    const s = svg(L, H);
    const barresS = series.filter((x) => x.type !== "ligne"), lignesS = series.filter((x) => x.type === "ligne");
    const toutes = [];
    categories.forEach((_, i) => {
      if (empile) {
        let pos = 0, neg = 0;
        for (const x of barresS) { const v = x.valeurs[i] ?? 0; v >= 0 ? (pos += v) : (neg += v); }
        toutes.push(pos, neg);
      } else barresS.forEach((x) => toutes.push(x.valeurs[i] ?? 0));
      lignesS.forEach((x) => x.valeurs[i] != null && toutes.push(x.valeurs[i]));
    });
    const e = echelle(toutes);
    const y = (v) => h + (H - h - b) * (1 - (v - e.min) / (e.max - e.min));
    for (const t of e.graduations) {
      el("line", { x1: g, x2: L - d, y1: y(t), y2: y(t), class: "grille-l" }, s);
      el("text", { x: g - 6, y: y(t) + 4, "text-anchor": "end" }, s).textContent = compact(t);
    }
    const largeurCat = (L - g - d) / categories.length;
    const nbGroupes = empile ? 1 : Math.max(barresS.length, 1);
    const lb = Math.max(2, Math.min(38, (largeurCat * 0.75) / nbGroupes));
    const pasEtiquette = Math.ceil(categories.length / Math.max(1, Math.floor((L - g) / 54)));
    categories.forEach((c, i) => {
      const x0 = g + i * largeurCat + (largeurCat - lb * nbGroupes) / 2;
      let pos = 0, neg = 0;
      barresS.forEach((x, k) => {
        const v = x.valeurs[i] ?? 0;
        let haut, bas;
        if (empile) { if (v >= 0) { bas = pos; pos += v; haut = pos; } else { haut = neg; neg += v; bas = neg; } }
        else { haut = Math.max(v, 0); bas = Math.min(v, 0); }
        const r = el("rect", {
          x: empile ? x0 : x0 + k * lb, y: y(haut), width: Math.max(1, lb - (empile ? 0 : 2)), height: Math.max(0, y(bas) - y(haut)),
          rx: 2, fill: x.couleur ?? COULEURS[series.indexOf(x) % 8],
        }, s);
        r.addEventListener("mousemove", (ev) => montrerBulle(ev, `<b>${echapper(c)}</b>${echapper(x.nom)} : ${format(v)}`));
        r.addEventListener("mouseleave", cacherBulle);
        if (surClic) { r.style.cursor = "pointer"; r.addEventListener("click", () => surClic(i, x)); }
      });
      if (i % pasEtiquette === 0) el("text", { x: g + i * largeurCat + largeurCat / 2, y: H - 6, "text-anchor": "middle" }, s).textContent = c;
    });
    for (const x of lignesS) {
      const points = x.valeurs.map((v, i) => (v == null ? null : [g + i * largeurCat + largeurCat / 2, y(v)]));
      const chemin = points.reduce((acc, p, i) => (p ? acc + `${acc && points[i - 1] ? "L" : "M"}${p[0]},${p[1]}` : acc), "");
      el("path", { d: chemin, fill: "none", stroke: x.couleur ?? "var(--texte)", "stroke-width": 2, "stroke-dasharray": x.pointille ? "5 4" : "" }, s);
      points.forEach((p, i) => {
        if (!p) return;
        const c = el("circle", { cx: p[0], cy: p[1], r: 3, fill: x.couleur ?? "var(--texte)" }, s);
        c.addEventListener("mousemove", (ev) => montrerBulle(ev, `<b>${echapper(categories[i])}</b>${echapper(x.nom)} : ${format(x.valeurs[i])}`));
        c.addEventListener("mouseleave", cacherBulle);
      });
    }
    zone.appendChild(s);
  });
}

/** Courbes (avec aire sous la première), pour la trésorerie ou un CA cumulé. */
export function courbes(conteneur, { categories, series, hauteur = 220, format = montant }) {
  conteneur.innerHTML = "";
  if (!categories.length) { conteneur.innerHTML = '<p class="vide">Aucune donnée.</p>'; return; }
  legende(conteneur, series);
  const zone = document.createElement("div");
  zone.className = "graphe";
  conteneur.appendChild(zone);
  suivre(zone, () => {
    zone.innerHTML = "";
    const L = Math.max(zone.clientWidth, 260), H = hauteur, g = 46, d = 10, h = 8, b = 22;
    const s = svg(L, H);
    const e = echelle(series.flatMap((x) => x.valeurs.filter((v) => v != null)));
    const y = (v) => h + (H - h - b) * (1 - (v - e.min) / (e.max - e.min));
    const x = (i) => g + (categories.length === 1 ? (L - g - d) / 2 : (i * (L - g - d)) / (categories.length - 1));
    for (const t of e.graduations) {
      el("line", { x1: g, x2: L - d, y1: y(t), y2: y(t), class: "grille-l" }, s);
      el("text", { x: g - 6, y: y(t) + 4, "text-anchor": "end" }, s).textContent = compact(t);
    }
    const pasEtiquette = Math.ceil(categories.length / Math.max(1, Math.floor((L - g) / 54)));
    categories.forEach((c, i) => { if (i % pasEtiquette === 0) el("text", { x: x(i), y: H - 6, "text-anchor": "middle" }, s).textContent = c; });
    series.forEach((serie, k) => {
      const couleur = serie.couleur ?? COULEURS[k % 8];
      const pts = serie.valeurs.map((v, i) => (v == null ? null : [x(i), y(v)]));
      const chemin = pts.reduce((acc, p, i) => (p ? acc + `${acc && pts[i - 1] ? "L" : "M"}${p[0]},${p[1]}` : acc), "");
      if (k === 0 && serie.aire !== false) {
        const valides = pts.filter(Boolean);
        if (valides.length > 1) el("path", { d: `${chemin}L${valides.at(-1)[0]},${y(Math.max(e.min, 0))}L${valides[0][0]},${y(Math.max(e.min, 0))}Z`, fill: couleur, opacity: 0.12 }, s);
      }
      el("path", { d: chemin, fill: "none", stroke: couleur, "stroke-width": 2, "stroke-dasharray": serie.pointille ? "5 4" : "" }, s);
      pts.forEach((p, i) => {
        if (!p) return;
        const c = el("circle", { cx: p[0], cy: p[1], r: 3, fill: couleur }, s);
        c.addEventListener("mousemove", (ev) => montrerBulle(ev, `<b>${echapper(categories[i])}</b>${series.map((z) => `${echapper(z.nom)} : ${z.valeurs[i] == null ? "–" : format(z.valeurs[i])}`).join("<br>")}`));
        c.addEventListener("mouseleave", cacherBulle);
      });
    });
    zone.appendChild(s);
  });
}

/** Barres horizontales classées (Top 10). items : [{ libelle, valeur, reference, detail, couleur }] */
export function barresH(conteneur, items, { format = montant, surClic, couleur = "var(--c1)", max } = {}) {
  if (!items.length) { conteneur.innerHTML = '<p class="vide">Aucune donnée.</p>'; return; }
  const m = max ?? Math.max(...items.map((i) => Math.max(Math.abs(i.valeur), Math.abs(i.reference ?? 0))), 1);
  conteneur.innerHTML = `<div class="barresh">${items.map((it, k) => `
    <div class="rang ${surClic ? "cliquable" : ""}" data-k="${k}" title="${echapper(it.libelle)}${it.detail ? " — " + echapper(it.detail) : ""}">
      <span class="nom">${echapper(it.libelle)}</span>
      <span class="piste"><span style="width:${(Math.abs(it.valeur) / m) * 100}%;background:${it.couleur ?? couleur}"></span>
        ${it.reference ? `<span class="n1" style="width:${(Math.abs(it.reference) / m) * 100}%"></span>` : ""}</span>
      <span class="num">${format(it.valeur)}</span>
    </div>`).join("")}</div>`;
  if (surClic) conteneur.querySelectorAll(".rang").forEach((r) => r.addEventListener("click", () => surClic(items[Number(r.dataset.k)])));
}

/** Anneau de répartition avec légende chiffrée. parts : [{ libelle, valeur }] */
export function anneau(conteneur, parts, { format = montant, surClic, max = 7 } = {}) {
  const positives = parts.filter((p) => p.valeur > 0).sort((a, b) => b.valeur - a.valeur);
  if (!positives.length) { conteneur.innerHTML = '<p class="vide">Aucune donnée.</p>'; return; }
  const gardees = positives.slice(0, max);
  const reste = positives.slice(max).reduce((t, p) => t + p.valeur, 0);
  if (reste > 0) gardees.push({ libelle: "Autres", valeur: reste });
  const total = gardees.reduce((t, p) => t + p.valeur, 0);
  conteneur.innerHTML = "";
  conteneur.className = (conteneur.className.replace(/\banneau\b/, "") + " anneau").trim();
  const s = svg(120, 120);
  s.setAttribute("width", "100%");
  let angle = -Math.PI / 2;
  gardees.forEach((p, i) => {
    const a = (p.valeur / total) * Math.PI * 2;
    const grand = a > Math.PI ? 1 : 0, R = 56, r = 36;
    const [x1, y1, x2, y2] = [60 + R * Math.cos(angle), 60 + R * Math.sin(angle), 60 + R * Math.cos(angle + a - 0.0001), 60 + R * Math.sin(angle + a - 0.0001)];
    const [x3, y3, x4, y4] = [60 + r * Math.cos(angle + a - 0.0001), 60 + r * Math.sin(angle + a - 0.0001), 60 + r * Math.cos(angle), 60 + r * Math.sin(angle)];
    const chemin = el("path", { d: `M${x1},${y1}A${R},${R} 0 ${grand} 1 ${x2},${y2}L${x3},${y3}A${r},${r} 0 ${grand} 0 ${x4},${y4}Z`, fill: COULEURS[i % 8] }, s);
    chemin.addEventListener("mousemove", (ev) => montrerBulle(ev, `<b>${echapper(p.libelle)}</b>${format(p.valeur)} · ${nombre((p.valeur / total) * 100, 1)} %`));
    chemin.addEventListener("mouseleave", cacherBulle);
    if (surClic && p.libelle !== "Autres") { chemin.style.cursor = "pointer"; chemin.addEventListener("click", () => surClic(p)); }
    angle += a;
  });
  el("text", { x: 60, y: 64, "text-anchor": "middle", style: "font-size:13px;font-weight:700;fill:var(--texte)" }, s).textContent = compact(total);
  const l = document.createElement("div");
  l.className = "legende";
  l.innerHTML = gardees.map((p, i) => `<div><span><i style="background:${COULEURS[i % 8]}"></i>${echapper(p.libelle)}</span><span class="num">${format(p.valeur)} <span class="discret">${nombre((p.valeur / total) * 100, 0)} %</span></span></div>`).join("");
  const boite = document.createElement("div");
  boite.appendChild(s);
  conteneur.append(boite, l);
}

/** Mini-courbe d'une tuile. */
export function miniCourbe(valeurs, couleur = "var(--accent)") {
  const v = valeurs.filter((x) => x != null);
  if (v.length < 2) return "";
  const min = Math.min(...v), max = Math.max(...v), e = max - min || 1;
  const pts = valeurs.map((x, i) => (x == null ? null : `${(i / (valeurs.length - 1)) * 80},${24 - ((x - min) / e) * 22}`)).filter(Boolean);
  return `<svg class="mini" viewBox="0 0 80 26"><polyline points="${pts.join(" ")}" fill="none" stroke="${couleur}" stroke-width="1.6"/></svg>`;
}
