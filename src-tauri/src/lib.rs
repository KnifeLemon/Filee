//! Filee-Lite core: conversion engines, route planner and job queue.
//!
//! Phase 1+2 of `REWRITE_DESIGN.md`. The Tauri shell (overlay window, donut UI,
//! global gesture hook) builds on top of [`JobQueue`] in later phases.

pub mod engines;
pub mod formats;
pub mod queue;
pub mod router;

pub use engines::{Engine, Tools};
pub use formats::FormatSlice;
pub use queue::{ConvertRequest, JobEvent, JobQueue};
pub use router::{Planner, Route, Step};
