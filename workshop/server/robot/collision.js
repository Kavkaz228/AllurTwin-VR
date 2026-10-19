import { array, norm, sub } from './math.js';
import { forwardKinematics } from './kinematics.js';

/** Slab intersection against an AABB, inflated by a capsule radius. */
export function segmentIntersectsBox(start, end, box, margin = 0) {
  const a = array(start), b = array(end), min = array(box.min), max = array(box.max);
  let entry = 0, exit = 1;
  for (let axis = 0; axis < 3; axis++) {
    const low = min[axis] - margin, high = max[axis] + margin;
    const delta = b[axis] - a[axis];
    if (Math.abs(delta) < 1e-12) {
      if (a[axis] < low || a[axis] > high) return false;
      continue;
    }
    let near = (low - a[axis]) / delta, far = (high - a[axis]) / delta;
    if (near > far) [near, far] = [far, near];
    entry = Math.max(entry, near);
    exit = Math.min(exit, far);
    if (entry > exit) return false;
  }
  return true;
}

export function checkConfiguration(config, joints, motionPadding = 0) {
  const state = forwardKinematics(config, joints);
  const low = array(config.workspace.min), high = array(config.workspace.max);
  for (let link = 0; link < state.links.length - 1; link++) {
    const radius = (link < config.joints.length ? config.joints[link].radius : config.toolRadius) + motionPadding;
    const a = state.links[link], b = state.links[link + 1];
    for (const endpoint of [array(a), array(b)]) {
      if (endpoint.some((value, axis) => value - radius < low[axis] || value + radius > high[axis])) {
        return { ok: false, code: 'WORKSPACE_COLLISION', error: `Link ${link} exits the configured workspace.`, link };
      }
    }
    for (const obstacle of config.obstacles) {
      if (segmentIntersectsBox(a, b, obstacle, radius)) {
        return { ok: false, code: 'OBSTACLE_COLLISION', error: `Link ${link} intersects obstacle ${obstacle.id}.`, link, obstacle: obstacle.id };
      }
    }
  }
  // Adjacent joint housings intentionally meet. Check separated structural capsules.
  for(let i=1;i<state.links.length-1;i++)for(let j=i+3;j<state.links.length-1;j++){
    const a=array(state.links[i]),b=array(state.links[i+1]),c=array(state.links[j]),d=array(state.links[j+1]);
    if(norm(sub(a,b))<1e-6||norm(sub(c,d))<1e-6)continue;
    const radius=(config.joints[i]?.radius??config.toolRadius)+(config.joints[j]?.radius??config.toolRadius)+2*motionPadding;
    if(segmentDistanceSquared(a,b,c,d)<radius*radius)return {ok:false,code:'SELF_COLLISION',error:`Separated robot links ${i} and ${j} collide.`,link:i,otherLink:j};
  }
  return { ok: true };
}

export function segmentDistanceSquared(a,b,c,d){
  const u=sub(b,a),v=sub(d,c),w=sub(a,c),dot=(x,y)=>x.reduce((s,n,i)=>s+n*y[i],0),A=dot(u,u),B=dot(u,v),C=dot(v,v),D=dot(u,w),E=dot(v,w);
  const clamp=x=>Math.max(0,Math.min(1,x));
  let s=0,t=0;
  if(A<1e-12)t=C>1e-12?clamp(E/C):0;
  else if(C<1e-12)s=clamp(-D/A);
  else {s=A*C-B*B>1e-12?clamp((B*E-C*D)/(A*C-B*B)):0;t=(B*s+E)/C;if(t<0){t=0;s=clamp(-D/A);}else if(t>1){t=1;s=clamp((B-D)/A);}}
  const delta=w.map((x,i)=>x+s*u[i]-t*v[i]);return dot(delta,delta);
}

/**
 * Check a straight joint-space path, including all links. Midpoint samples are
 * inflated by a conservative motion bound, so a thin obstacle between samples
 * cannot be skipped. This intentionally can reject paths near an obstacle.
 * It is a simulation guard, not a certified robot safety function.
 */
export function checkJointPath(config, from, to) {
  const deltas = to.map((v, i) => v - from[i]);
  // Finer samples also bound the relative sweep of separated link capsules.
  const count = Math.max(1, Math.ceil(Math.max(...deltas.map(Math.abs)) / Math.min(config.pathStepRadians,.01)));
  if (count > config.maxPathSamples) return { ok: false, code: 'PATH_TOO_COMPLEX', error: 'Joint path exceeds the configured bounded collision-check budget.' };
  for (const q of [from, to]) {
    const check = checkConfiguration(config, q);
    if (!check.ok) return check;
  }
  const reach = config.joints.reduce((sum, joint) => sum + norm(joint.offset), norm(array(config.toolOffset)));
  // A point anywhere on the arm moves by no more than reach * sum(|dq|).
  const padding = reach * deltas.reduce((sum, value) => sum + Math.abs(value), 0) / (2 * count);
  for (let sample = 0; sample < count; sample++) {
    const fraction = (sample + 0.5) / count;
    const q = from.map((value, i) => value + deltas[i] * fraction);
    const check = checkConfiguration(config, q, padding);
    if (!check.ok) return { ...check, fraction };
  }
  return { ok: true, samples: count };
}
