//! Cheapest route search over the `petgraph` conversion graph.
//!
//! Plain Dijkstra cannot enforce the 3-hop limit, so this is a hop-bounded
//! Bellman-Ford: layer `k` holds the cheapest way to reach each format in exactly
//! `k` hops. The graph has a few dozen nodes, so this costs microseconds.

use std::collections::{BTreeSet, HashMap};

use petgraph::graph::{DiGraph, EdgeIndex, NodeIndex};
use petgraph::visit::EdgeRef;
use serde::Serialize;

use crate::engines::{Engine, Tools};
use crate::formats::{self, FormatSlice};

pub const MAX_HOPS: usize = 3;

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct Step {
    pub from: String,
    pub to: String,
    pub engine: Engine,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct Route {
    pub steps: Vec<Step>,
    pub cost: u32,
}

impl std::fmt::Display for Route {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if let Some(first) = self.steps.first() {
            write!(f, "{}", first.from)?;
        }
        for s in &self.steps {
            write!(f, " -[{}]-> {}", s.engine.name(), s.to)?;
        }
        write!(f, "  (cost {})", self.cost)
    }
}

#[derive(Clone, Copy)]
struct Edge {
    engine: Engine,
    cost: u32,
}

/// `layers[k][node]` = cheapest cost to reach `node` in exactly `k` hops and the edge used.
type Layers = Vec<Vec<Option<(u32, Option<EdgeIndex>)>>>;

pub struct Planner {
    graph: DiGraph<String, Edge>,
    nodes: HashMap<String, NodeIndex>,
}

impl Planner {
    /// Builds the graph from the engines available with `tools`.
    pub fn new(tools: &Tools) -> Self {
        let mut graph = DiGraph::new();
        let mut nodes = HashMap::new();
        for (from, to, engine, cost) in super::edges() {
            if !tools.has(engine) {
                continue;
            }
            let mut node = |name: &str| {
                *nodes.entry(name.to_string()).or_insert_with(|| graph.add_node(name.to_string()))
            };
            let (a, b) = (node(from), node(to));
            graph.add_edge(a, b, Edge { engine, cost });
        }
        Self { graph, nodes }
    }

    /// Cheapest route of at most [`MAX_HOPS`] steps; ties go to fewer steps.
    pub fn plan(&self, from: &str, to: &str) -> Option<Route> {
        let (from, to) = (formats::normalize(from), formats::normalize(to));
        if from == to {
            return None;
        }
        let (&s, &t) = (self.nodes.get(&from)?, self.nodes.get(&to)?);
        let layers = self.layers(s);
        let (hops, cost) = (1..=MAX_HOPS)
            .filter_map(|k| layers[k][t.index()].map(|(c, _)| (k, c)))
            .min_by_key(|&(k, c)| (c, k))?;

        // A cheapest path never contains a cycle (dropping it would be cheaper and shorter),
        // so walking predecessors back from layer `hops` yields a simple path.
        let mut steps = Vec::with_capacity(hops);
        let mut node = t;
        for k in (1..=hops).rev() {
            let edge = layers[k][node.index()]?.1?;
            let (src, dst) = self.graph.edge_endpoints(edge)?;
            debug_assert_eq!(dst, node);
            steps.push(Step {
                from: self.graph[src].clone(),
                to: self.graph[dst].clone(),
                engine: self.graph[edge].engine,
            });
            node = src;
        }
        steps.reverse();
        Some(Route { steps, cost })
    }

    /// Every format reachable from `from` within [`MAX_HOPS`] steps.
    pub fn targets(&self, from: &str) -> BTreeSet<String> {
        let from = formats::normalize(from);
        let Some(&s) = self.nodes.get(&from) else { return BTreeSet::new() };
        let layers = self.layers(s);
        self.graph
            .node_indices()
            .filter(|&n| n != s && (1..=MAX_HOPS).any(|k| layers[k][n.index()].is_some()))
            .map(|n| self.graph[n].clone())
            .collect()
    }

    /// Donut slices for a drag of files with these extensions: the targets every file can reach.
    pub fn available_targets<S: AsRef<str>>(&self, extensions: &[S]) -> Vec<FormatSlice> {
        let mut common: Option<BTreeSet<String>> = None;
        for ext in extensions {
            let t = self.targets(ext.as_ref());
            common = Some(match common {
                None => t,
                Some(c) => c.intersection(&t).cloned().collect(),
            });
        }
        let mut slices: Vec<FormatSlice> =
            common.unwrap_or_default().iter().map(|id| FormatSlice::new(id)).collect();
        slices.sort_by(|a, b| {
            (formats::category_rank(a.category), &a.id).cmp(&(formats::category_rank(b.category), &b.id))
        });
        slices
    }

    fn layers(&self, source: NodeIndex) -> Layers {
        let n = self.graph.node_count();
        let mut layers: Layers = vec![vec![None; n]; MAX_HOPS + 1];
        layers[0][source.index()] = Some((0, None));
        for k in 1..=MAX_HOPS {
            for e in self.graph.edge_references() {
                let Some((cost, _)) = layers[k - 1][e.source().index()] else { continue };
                let next = cost + e.weight().cost;
                let slot = &mut layers[k][e.target().index()];
                if slot.map_or(true, |(c, _)| next < c) {
                    *slot = Some((next, Some(e.id())));
                }
            }
        }
        layers
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn all_tools() -> Tools {
        Tools { soffice: Some("soffice".into()), pandoc: Some("pandoc".into()), ffmpeg: Some("ffmpeg".into()), pdfium: true }
    }

    fn engines(r: &Route) -> Vec<(&str, Engine)> {
        r.steps.iter().map(|s| (s.to.as_str(), s.engine)).collect()
    }

    #[test]
    fn hwp_to_png_goes_through_html_and_pdf() {
        let p = Planner::new(&all_tools());
        let r = p.plan("hwp", "png").unwrap();
        assert_eq!(
            engines(&r),
            vec![("html", Engine::UnhwpHtml), ("pdf", Engine::LibreOffice), ("png", Engine::Pdfium)]
        );
    }

    #[test]
    fn direct_routes_win() {
        let p = Planner::new(&all_tools());
        assert_eq!(engines(&p.plan("hwpx", "md").unwrap()), vec![("md", Engine::Unhwp)]);
        assert_eq!(engines(&p.plan("xls", "xlsx").unwrap()), vec![("xlsx", Engine::Spreadsheet)]);
        assert_eq!(engines(&p.plan("jpeg", "webp").unwrap()), vec![("webp", Engine::Raster)]);
    }

    #[test]
    fn prefers_pure_rust_over_pandoc() {
        let p = Planner::new(&all_tools());
        // docx -> md -> html (1 + 1) beats pandoc (3).
        assert_eq!(
            engines(&p.plan("docx", "html").unwrap()),
            vec![("md", Engine::WordText), ("html", Engine::Markdown)]
        );
    }

    #[test]
    fn chains_through_libreoffice() {
        let p = Planner::new(&all_tools());
        assert_eq!(
            engines(&p.plan("doc", "txt").unwrap()),
            vec![("docx", Engine::LibreOffice), ("txt", Engine::WordText)]
        );
        assert_eq!(engines(&p.plan("hwp", "docx").unwrap()), vec![("md", Engine::Unhwp), ("docx", Engine::Pandoc)]);
    }

    #[test]
    fn respects_hop_limit_and_identity() {
        let p = Planner::new(&all_tools());
        // hwp -> html -> pdf -> png -> jpg would need 4 hops.
        assert!(p.plan("hwp", "jpg").is_none());
        assert!(p.plan("png", "png").is_none());
        assert!(p.plan("png", "zzz").is_none());
    }

    #[test]
    fn missing_tools_remove_routes() {
        let p = Planner::new(&Tools::default());
        assert!(p.plan("hwp", "pdf").is_none());
        assert!(p.plan("mp4", "mp3").is_none());
        let ids: Vec<String> = p.available_targets(&["hwp"]).into_iter().map(|s| s.id).collect();
        assert_eq!(ids, ["html", "json", "md", "txt"]);
    }

    #[test]
    fn targets_are_intersected() {
        let p = Planner::new(&Tools::default());
        let ids: Vec<String> = p.available_targets(&["png", "JPEG", "svg"]).into_iter().map(|s| s.id).collect();
        // svg reaches png directly and the rest via png; png itself is not a target of png.
        assert_eq!(ids, ["bmp", "ico", "webp"]);
        assert!(p.available_targets::<&str>(&[]).is_empty());
    }
}
