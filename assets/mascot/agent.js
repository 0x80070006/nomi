/**
 * Contrôleur d'état du Petit explorateur.
 * Importer le fichier, puis créer new AgentMascot(svgElement).
 */
export class AgentMascot {
  constructor(svg, options = {}) {
    this.svg = svg;
    this.reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    this.state = "idle";
    this.timer = null;
    this.options = { oneShotMs: 1300, ...options };
    this.apply("idle");
  }

  setState(state, { duration } = {}) {
    const known = ["idle","listen","think","speak","success","error","surprise","sleep","loading","greeting","drag","edge"];
    if (!known.includes(state)) throw new Error(`État inconnu : ${state}`);
    clearTimeout(this.timer);
    this.state = state;
    this.svg.dataset.state = state;
    this.svg.classList.remove("state-idle","state-listen","state-think","state-speak","state-success","state-error","state-surprise","state-sleep","state-loading","state-greeting","state-drag","state-edge");
    this.svg.classList.add(`state-${state}`);
    this.apply(state);
    const oneShot = ["success","error","surprise","greeting"];
    if (oneShot.includes(state)) {
      this.timer = setTimeout(() => this.setState("idle"), duration ?? this.options.oneShotMs);
    }
  }

  apply(state) {
    // SVG autonome : les classes CSS pilotent l'animation et les poses.
    if (this.reducedMotion) this.svg.classList.add("reduced-motion");
    else this.svg.classList.remove("reduced-motion");
    this.svg.setAttribute("aria-label", `Mascotte : ${state}`);
  }

  setVoiceLevel(level) {
    // Niveau attendu entre 0 et 1 ; utilisé uniquement pendant l'écoute.
    const safe = Math.max(0, Math.min(1, Number(level) || 0));
    this.svg.style.setProperty("--voice-level", safe.toFixed(2));
  }

  setDragVector(x, y) {
    // x/y normalisés dans [-1,1], pour donner une direction à l'étirement.
    this.svg.style.setProperty("--drag-x", Math.max(-1, Math.min(1, x)));
    this.svg.style.setProperty("--drag-y", Math.max(-1, Math.min(1, y)));
  }

  destroy() {
    clearTimeout(this.timer);
    this.svg.classList.remove(...Array.from(this.svg.classList).filter(c => c.startsWith("state-")));
  }
}
