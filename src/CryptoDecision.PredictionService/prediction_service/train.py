"""
Standalone XGBoost training script.

Usage (inside container):
    python -m prediction_service.train

Usage (from host):
    docker exec prediction python -m prediction_service.train

Algorithm
---------
1. Query daily_feature_table for last 180 days.
2. Join each day with the next day's return_24h to derive the label:
      return_24h_tomorrow >  0.5%  →  UP
      return_24h_tomorrow < -0.5%  →  DOWN
      otherwise                    →  NEUTRAL
3. Train XGBClassifier (200 trees, max_depth=4, learning_rate=0.05).
4. Require >= 30 labeled samples; exit without overwriting model if insufficient.
5. Save to /app/models/model.pkl via joblib.
6. Hot-reload model in the running service if imported as a module.
"""
from __future__ import annotations

import logging
import os
import sys

log = logging.getLogger(__name__)
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

MODEL_PATH   = os.environ.get("MODEL_PATH", "/app/models/model.pkl")
POSTGRES_URL = os.environ.get("POSTGRES_URL", "postgresql://crypto:crypto@postgres:5432/crypto")
MIN_SAMPLES  = 30

_SQL = """
    SELECT
        d1.return_24h,
        d1.volume_change,
        d1.whale_count,
        d1.volatility,
        CASE
            WHEN d2.return_24h >  0.5 THEN 'UP'
            WHEN d2.return_24h < -0.5 THEN 'DOWN'
            ELSE 'NEUTRAL'
        END AS label
    FROM daily_feature_table d1
    JOIN daily_feature_table d2
        ON d1.symbol = d2.symbol
       AND d2.date = d1.date + INTERVAL '1 day'
    WHERE d1.date >= CURRENT_DATE - INTERVAL '180 days'
      AND d2.date <  CURRENT_DATE
    ORDER BY d1.date
"""


def _fetch_data() -> tuple[list[list[float]], list[str]]:
    import psycopg2
    import psycopg2.extras

    conn = psycopg2.connect(POSTGRES_URL)
    try:
        with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
            cur.execute(_SQL)
            rows = cur.fetchall()
    finally:
        conn.close()

    X = [
        [
            float(r["return_24h"] or 0.0),
            float(r["volume_change"] or 0.0),
            float(r["whale_count"] or 0),
            float(r["volatility"] or 0.0),
        ]
        for r in rows
    ]
    y = [r["label"] for r in rows]
    return X, y


def readiness() -> tuple[int, int, int]:
    """
    Labeled samples available, the minimum required, and how many symbols supply them.

    Split out so startup can report it without importing xgboost or touching the
    model file. The weekly retrain already logged its refusal to train, but it
    only speaks at 02:00 on a Sunday — so XGBoost abstained from every prediction
    for six days and the only trace was a model_version string. Asking this
    question on every boot makes a permanently-absent model say so.

    The symbol count matters for reading the shortfall, and is easy to get wrong:
    _SQL deliberately does not filter by symbol, so every symbol with features
    contributes a row per day. With three symbols the table gains three samples a
    day, not one, and "27 samples short" is nine days away rather than twenty-seven.

    Labels need the *following* day's return, so N days of features yield at most
    N-1 samples per symbol; that is why this counts the join, not the feature table.
    """
    import psycopg2

    X, _ = _fetch_data()

    conn = psycopg2.connect(POSTGRES_URL)
    try:
        with conn.cursor() as cur:
            cur.execute("SELECT COUNT(DISTINCT symbol) FROM daily_feature_table")
            symbols = int(cur.fetchone()[0] or 0)
    finally:
        conn.close()

    return len(X), MIN_SAMPLES, symbols


def train() -> None:
    log.info("Fetching training data from PostgreSQL (%s)...", POSTGRES_URL)
    X, y = _fetch_data()
    n = len(X)
    log.info("Fetched %d labeled samples", n)

    if n < MIN_SAMPLES:
        log.warning(
            "Only %d samples (minimum %d required). "
            "Skipping training — existing model (if any) unchanged.",
            n, MIN_SAMPLES,
        )
        return

    from xgboost import XGBClassifier
    from sklearn.preprocessing import LabelEncoder
    from sklearn.metrics import accuracy_score
    from sklearn.model_selection import StratifiedKFold, cross_val_score
    import optuna
    
    # Optuna is extremely verbose by default, suppress its logging
    optuna.logging.set_verbosity(optuna.logging.WARNING)

    # Fix class order: 0=DOWN, 1=NEUTRAL, 2=UP — must match ml_model._CLASSES
    le = LabelEncoder()
    le.fit(["DOWN", "NEUTRAL", "UP"])
    y_enc = le.transform(y)

    def objective(trial):
        params = {
            "n_estimators": trial.suggest_int("n_estimators", 50, 300),
            "max_depth": trial.suggest_int("max_depth", 3, 9),
            "learning_rate": trial.suggest_float("learning_rate", 0.01, 0.3, log=True),
            "subsample": trial.suggest_float("subsample", 0.5, 1.0),
            "colsample_bytree": trial.suggest_float("colsample_bytree", 0.5, 1.0),
            "eval_metric": "mlogloss",
            "random_state": 42,
            "n_jobs": 1,
        }
        model = XGBClassifier(**params)
        cv = StratifiedKFold(n_splits=3, shuffle=True, random_state=42)
        # We use accuracy as the metric to maximize
        scores = cross_val_score(model, X, y_enc, cv=cv, scoring="accuracy")
        return scores.mean()

    log.info("Starting Optuna hyperparameter tuning (20 trials)...")
    study = optuna.create_study(direction="maximize")
    study.optimize(objective, n_trials=20)
    
    best_params = study.best_params
    log.info("Best parameters found: %s", best_params)
    log.info("Best cross-validation accuracy: %.4f", study.best_value)

    # Train final model on ALL data using best params
    final_params = {**best_params, "eval_metric": "mlogloss", "random_state": 42, "n_jobs": 1}
    clf = XGBClassifier(**final_params)
    clf.fit(X, y_enc)
    clf.classes_ = le.classes_      # attach class labels for predict_proba ordering

    acc = accuracy_score(y_enc, clf.predict(X))
    log.info("Final model trained — in-sample accuracy: %.4f (n=%d samples)", acc, n)

    os.makedirs(os.path.dirname(MODEL_PATH) or ".", exist_ok=True)

    import joblib
    joblib.dump(clf, MODEL_PATH)
    log.info("Model saved to %s", MODEL_PATH)

    # Hot-reload the running service's in-memory model (when called from within the service)
    try:
        from . import ml_model
        ml_model.reload()
        log.info("Model hot-reloaded in running service process")
    except ImportError:
        pass   # running as __main__ standalone script


if __name__ == "__main__":
    train()
    sys.exit(0)
