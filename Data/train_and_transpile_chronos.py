#!/usr/bin/env python3
"""
Project Chronos: Decentralized Latency Compensation Framework
Real Dataset + User-Recorded Live Gameplay Pre-Processing & Per-Player Model Training Pipeline

Data Sources:
  1. Base CS:GO Dataset: Data/RawBehavior/csgo_round_snapshots.csv (122,410 rows -> 12,000 baseline vectors)
  2. Player 0 Live User-Recorded Telemetry: Client_TopDownShooter/Assets/TelemetryLogs/player_0_telemetry.csv
     (6,809 raw samples recorded across 3 live user gameplay sessions -> pre-processed active moving & shooting frames)
  3. Player 1 Static Peer Telemetry: Client_TopDownShooter/Assets/TelemetryLogs/player_1_telemetry.csv
     (720 samples preserved untouched as requested)
"""

import csv
import json
import math
import os
import random

RANDOM_STATE = 42
NUM_CLASSES = 4  # 0=Retreat, 1=HoldAngle, 2=AggressiveFlank, 3=PassiveStationary

FEATURE_COLUMNS = [
    "Distance_To_Enemy",
    "Enemy_Visible",
    "Player_HP",
    "Player_Ammo",
    "In_Chokepoint",
]


def load_and_clean_real_csgo_dataset(csv_path: str, max_samples: int = 12000):
    rng = random.Random(RANDOM_STATE)
    total_raw_rows = 0
    missing_null_rows = 0
    invalid_zero_alive_rows = 0
    missing_target_rows = 0
    valid_raw_rows = []

    required_cols = [
        "time_left", "bomb_planted", "ct_health", "t_health",
        "ct_armor", "t_armor", "ct_players_alive", "t_players_alive",
        "map", "round_winner"
    ]

    with open(csv_path, "r", newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            total_raw_rows += 1
            if any(row.get(c) is None or str(row.get(c)).strip() == "" for c in required_cols):
                missing_null_rows += 1
                continue

            if row["round_winner"] not in ("CT", "T"):
                missing_target_rows += 1
                continue

            try:
                ct_alive = float(row["ct_players_alive"])
                t_alive = float(row["t_players_alive"])
                ct_hp = float(row["ct_health"])
                t_hp = float(row["t_health"])
                time_left = float(row["time_left"])
            except ValueError:
                missing_null_rows += 1
                continue

            if ct_alive <= 0 or t_alive <= 0 or ct_hp <= 0 or t_hp <= 0 or time_left < 0:
                invalid_zero_alive_rows += 1
                continue

            valid_raw_rows.append(row)

    print("=== 1. Base CS:GO Dataset Quality & Missing Value Report ===")
    print(f"Total raw rows read         : {total_raw_rows}")
    print(f"Missing / NULL value rows   : {missing_null_rows} (dropped)")
    print(f"Missing target rows         : {missing_target_rows} (dropped)")
    print(f"Invalid rows (0 alive / <=0): {invalid_zero_alive_rows} (dropped)")
    print(f"Clean valid rows retained   : {len(valid_raw_rows)}")

    rng.shuffle(valid_raw_rows)
    sampled_raw = valid_raw_rows[:max_samples]

    processed_rows = []
    for r in sampled_raw:
        ct_alive = float(r["ct_players_alive"])
        t_alive = float(r["t_players_alive"])
        ct_hp_total = float(r["ct_health"])
        t_hp_total = float(r["t_health"])
        ct_armor = float(r["ct_armor"])
        t_armor = float(r["t_armor"])
        time_left = float(r["time_left"])
        bomb = 1 if str(r["bomb_planted"]).lower() == "true" else 0

        player_hp = round(min(100.0, max(1.0, ct_hp_total / ct_alive)), 2)

        long_range = (
            float(r.get("ct_weapon_awp", 0)) + float(r.get("t_weapon_awp", 0)) +
            float(r.get("ct_weapon_ssg08", 0)) + float(r.get("ct_weapon_ssg08", 0)) +
            float(r.get("ct_weapon_aug", 0)) + float(r.get("t_weapon_sg553", 0))
        )
        rifles = (
            float(r.get("ct_weapon_ak47", 0)) + float(r.get("ct_weapon_m4a4", 0)) +
            float(r.get("ct_weapon_m4a1s", 0)) + float(r.get("ct_weapon_famas", 0))
        )
        close_range = (
            float(r.get("ct_weapon_mp9", 0)) + float(r.get("ct_weapon_mac10", 0)) +
            float(r.get("ct_weapon_ump45", 0)) + float(r.get("ct_weapon_p90", 0)) +
            float(r.get("ct_weapon_xm1014", 0)) + float(r.get("ct_weapon_mag7", 0))
        )
        pistols = (
            float(r.get("ct_weapon_usps", 0)) + float(r.get("ct_weapon_glock", 0)) +
            float(r.get("ct_weapon_deagle", 0)) + float(r.get("ct_weapon_p250", 0))
        )
        utility = (
            float(r.get("ct_grenade_smokegrenade", 0)) +
            float(r.get("ct_grenade_flashbang", 0)) +
            float(r.get("ct_grenade_hegrenade", 0)) +
            float(r.get("ct_grenade_incendiarygrenade", 0)) +
            float(r.get("t_grenade_molotovgrenade", 0))
        )

        raw_ammo = ((rifles * 8.0 + close_range * 7.0 + long_range * 5.0 + pistols * 3.0 + utility * 1.5) / ct_alive)
        player_ammo = round(min(30.0, max(0.0, raw_ammo)), 2)

        base_dist = (time_left / 175.0) * 16.0 + (2.5 if long_range > close_range else -1.5) - (4.0 if bomb else 0.0)
        dist = round(min(25.0, max(1.5, base_dist + (ct_armor - t_armor) * 0.005)), 4)

        damage_exchanged = (ct_hp_total < ct_alive * 98.0) or (t_hp_total < t_alive * 98.0)
        visible = 1 if (damage_exchanged or bomb == 1 or dist < 12.0) else 0

        choke_map = r["map"] in ("de_inferno", "de_nuke", "de_overpass", "de_mirage")
        in_chokepoint = 1 if (bomb == 1 or (choke_map and utility >= 2.0 and time_left < 95.0)) else 0

        alive_diff = ct_alive - t_alive
        if player_hp <= 38.0 or player_ammo <= 3.2 or (alive_diff <= -2 and player_hp < 70.0):
            intent = 0  # RETREAT
        elif in_chokepoint == 1 or (dist >= 10.5 and visible == 1 and alive_diff <= 0):
            intent = 1  # HOLD_ANGLE
        else:
            intent = 2  # AGGRESSIVE_FLANK

        angle = ((ct_hp_total * 0.13 + t_hp_total * 0.07 + time_left * 0.31) % (2.0 * math.pi)) - math.pi
        vx = round(math.cos(angle) * dist, 4)
        vy = round(math.sin(angle) * dist, 4)
        mag = math.sqrt(vx * vx + vy * vy) or 1.0
        nx, ny = vx / mag, vy / mag

        if intent == 0:
            mx, my = -nx, -ny
            shoot = 1 if (visible == 1 and dist < 8.0 and player_ammo > 0) else 0
        elif intent == 1:
            mx, my = -ny * 0.5, nx * 0.5
            shoot = 1 if (visible == 1 and dist <= 20.0 and player_ammo > 0) else 0
        else:
            rx = nx * 0.85 - ny * 0.35
            ry = ny * 0.85 + nx * 0.35
            rm = math.sqrt(rx * rx + ry * ry) or 1.0
            mx, my = rx / rm, ry / rm
            shoot = 1 if (visible == 1 and dist <= 16.0 and player_ammo > 0) else 0

        processed_rows.append({
            "Player_ID": -1,
            "Distance_To_Enemy": dist,
            "Enemy_Visible": visible,
            "Player_HP": player_hp,
            "Player_Ammo": player_ammo,
            "In_Chokepoint": in_chokepoint,
            "Intent_Label": intent,
            "Vector_X_To_Enemy": vx,
            "Vector_Y_To_Enemy": vy,
            "Move_Input_X": round(mx, 4),
            "Move_Input_Y": round(my, 4),
            "Trigger_Shoot": shoot,
        })

    return processed_rows


def load_and_preprocess_recorded_telemetry(telemetry_dir: str):
    """
    Pre-processes the user's live recorded telemetry in player_0_telemetry.csv:
      1. Inspects all raw rows, drops missing/corrupted rows, and filters idle key-release pause ticks
         (|mx| < 0.02 and |my| < 0.02 and shoot == 0) from Player 0 so Player 0's model is 100% active moving & shooting.
      2. Aligns Player 0's tactical intent labels with the user's recorded +Z laser combat distance zones:
         - dist < 5.0m -> Intent 0 (Retreat/Back up to laser firing position below opponent)
         - 5.0m <= dist <= 7.2m -> Intent 1 (HoldAngle/Strafe & weave in firing sweet spot)
         - dist > 7.2m -> Intent 2 (AggressiveFlank/Advance forward along +Y toward opponent)
      3. Preserves player_1_telemetry.csv (static player data) untouched as requested.
    """
    p0_raw = 0
    p0_missing = 0
    p0_idle_filtered = 0
    p1_raw = 0
    clean_by_player = {0: [], 1: []}

    # 1. Load & Pre-Process User's Recorded Player 0 Data
    p0_csv = os.path.join(telemetry_dir, "player_0_telemetry.csv")
    if os.path.exists(p0_csv):
        with open(p0_csv, "r", newline="", encoding="utf-8") as f:
            reader = csv.DictReader(f)
            for row in reader:
                p0_raw += 1
                try:
                    dist = float(row["Distance_To_Enemy"])
                    vis = int(float(row["Enemy_Visible"]))
                    hp = float(row["Player_HP"])
                    ammo = float(row["Player_Ammo"])
                    choke = int(float(row["In_Chokepoint"]))
                    vx = float(row["Vector_X_To_Enemy"])
                    vy = float(row["Vector_Y_To_Enemy"])
                    mx = float(row["Move_Input_X"])
                    my = float(row["Move_Input_Y"])
                    shoot = int(float(row["Trigger_Shoot"]))
                except (KeyError, TypeError, ValueError):
                    p0_missing += 1
                    continue

                if hp <= 0.0 or dist < 0.20 or dist > 60.0:
                    p0_idle_filtered += 1
                    continue

                # Filter out brief unpressed-key pause ticks between keystrokes on Player 0
                # so the trained Player 0 model never freezes in place when taking over
                if abs(mx) < 0.02 and abs(my) < 0.02 and shoot == 0:
                    p0_idle_filtered += 1
                    continue

                # Map user's recorded combat zones to deterministic tactical intents:
                # - Close (< 5.0m): User backs up along -Y to position below static opponent (Retreat = 0)
                # - Sweet spot (5.0m .. 7.2m): User strafes/weaves X & Y keeping +Z laser on target (HoldAngle = 1)
                # - Far (> 7.2m): User advances forward along +Y toward opponent (AggressiveFlank = 2)
                if dist < 5.0:
                    intent = 0
                elif dist <= 7.2:
                    intent = 1
                else:
                    intent = 2

                clean_by_player[0].append({
                    "Player_ID": 0,
                    "Distance_To_Enemy": round(dist, 4),
                    "Enemy_Visible": 1 if vis > 0 else 0,
                    "Player_HP": round(hp, 2),
                    "Player_Ammo": round(ammo, 2),
                    "In_Chokepoint": 1 if choke > 0 else 0,
                    "Intent_Label": intent,
                    "Vector_X_To_Enemy": round(vx, 4),
                    "Vector_Y_To_Enemy": round(vy, 4),
                    "Move_Input_X": round(mx, 4),
                    "Move_Input_Y": round(my, 4),
                    "Trigger_Shoot": 1 if shoot > 0 else 0,
                })

    # 2. Load Static Player 1 Data Untouched
    p1_csv = os.path.join(telemetry_dir, "player_1_telemetry.csv")
    if os.path.exists(p1_csv):
        with open(p1_csv, "r", newline="", encoding="utf-8") as f:
            reader = csv.DictReader(f)
            for row in reader:
                p1_raw += 1
                clean_by_player[1].append({
                    "Player_ID": 1,
                    "Distance_To_Enemy": round(float(row["Distance_To_Enemy"]), 4),
                    "Enemy_Visible": int(float(row["Enemy_Visible"])),
                    "Player_HP": round(float(row["Player_HP"]), 2),
                    "Player_Ammo": round(float(row["Player_Ammo"]), 2),
                    "In_Chokepoint": int(float(row["In_Chokepoint"])),
                    "Intent_Label": int(float(row["Intent_Label"])),
                    "Vector_X_To_Enemy": round(float(row["Vector_X_To_Enemy"]), 4),
                    "Vector_Y_To_Enemy": round(float(row["Vector_Y_To_Enemy"]), 4),
                    "Move_Input_X": round(float(row["Move_Input_X"]), 4),
                    "Move_Input_Y": round(float(row["Move_Input_Y"]), 4),
                    "Trigger_Shoot": int(float(row["Trigger_Shoot"])),
                })

    print("\n=== 2. User-Recorded Live Gameplay Telemetry Pre-Processing Report ===")
    print(f"Player 0 raw user-recorded rows inspected          : {p0_raw}")
    print(f"Player 0 missing / malformed rows dropped          : {p0_missing}")
    print(f"Player 0 idle key-transition pause rows filtered   : {p0_idle_filtered}")
    print(f"Clean Player 0 active moving/shooting rows retained: {len(clean_by_player[0])}")
    print(f"Clean Player 1 static rows preserved untouched     : {len(clean_by_player[1])}")

    return clean_by_player


class TreeNode:
    def __init__(self, feature_idx=None, threshold=None, left=None, right=None, value=None):
        self.feature_idx = feature_idx
        self.threshold = threshold
        self.left = left
        self.right = right
        self.value = value


def gini_impurity(labels, n_classes=NUM_CLASSES):
    n = len(labels)
    if n == 0:
        return 0.0
    counts = [0] * n_classes
    for y in labels:
        counts[y] += 1
    return 1.0 - sum((c / n) ** 2 for c in counts)


def build_tree(X, y, rng, depth=0, max_depth=5, min_samples_split=8, max_features=4):
    n = len(y)
    counts = [0] * NUM_CLASSES
    for label in y:
        counts[label] += 1
    majority_class = max(range(NUM_CLASSES), key=lambda c: (counts[c], -c))

    if depth >= max_depth or n < min_samples_split or len(set(y)) == 1:
        return TreeNode(value=majority_class)

    n_features = len(X[0])
    feature_indices = rng.sample(range(n_features), min(max_features, n_features))

    best_gain = 0.0
    best_feat = None
    best_thresh = None
    best_left = None
    best_right = None
    parent_gini = gini_impurity(y)

    for feat in feature_indices:
        vals = sorted(set(row[feat] for row in X))
        if len(vals) <= 1:
            continue
        step = max(1, len(vals) // 16)
        candidates = [(vals[i] + vals[min(i + 1, len(vals) - 1)]) / 2.0 for i in range(0, len(vals) - 1, step)]
        for thresh in candidates:
            left_idx = [i for i in range(n) if X[i][feat] <= thresh]
            right_idx = [i for i in range(n) if X[i][feat] > thresh]
            if not left_idx or not right_idx:
                continue
            y_left = [y[i] for i in left_idx]
            y_right = [y[i] for i in right_idx]
            gain = parent_gini - (
                (len(y_left) / n) * gini_impurity(y_left)
                + (len(y_right) / n) * gini_impurity(y_right)
            )
            if gain > best_gain:
                best_gain = gain
                best_feat = feat
                best_thresh = round(thresh, 4)
                best_left = left_idx
                best_right = right_idx

    if best_feat is None:
        return TreeNode(value=majority_class)

    left_node = build_tree(
        [X[i] for i in best_left],
        [y[i] for i in best_left],
        rng,
        depth + 1,
        max_depth,
        min_samples_split,
        max_features,
    )
    right_node = build_tree(
        [X[i] for i in best_right],
        [y[i] for i in best_right],
        rng,
        depth + 1,
        max_depth,
        min_samples_split,
        max_features,
    )
    return TreeNode(feature_idx=best_feat, threshold=best_thresh, left=left_node, right=right_node)


def predict_tree(node: TreeNode, x):
    while node.value is None:
        if x[node.feature_idx] <= node.threshold:
            node = node.left
        else:
            node = node.right
    return node.value


def predict_forest(trees, x):
    votes = [0] * NUM_CLASSES
    for t in trees:
        votes[predict_tree(t, x)] += 1
    best_class = 0
    best_votes = votes[0]
    for c in range(1, NUM_CLASSES):
        if votes[c] > best_votes:
            best_votes = votes[c]
            best_class = c
    return best_class


def evaluate_predictions(y_true, y_pred, n_classes=NUM_CLASSES):
    conf = [[0] * n_classes for _ in range(n_classes)]
    correct = 0
    for yt, yp in zip(y_true, y_pred):
        conf[yt][yp] += 1
        if yt == yp:
            correct += 1
    acc = correct / len(y_true) if y_true else 0.0
    active_classes = [c for c in range(n_classes) if sum(conf[c]) > 0 or sum(conf[r][c] for r in range(n_classes)) > 0]
    f1s = []
    for c in active_classes:
        tp = conf[c][c]
        fp = sum(conf[r][c] for r in range(n_classes) if r != c)
        fn = sum(conf[c][col] for col in range(n_classes) if col != c)
        prec = tp / (tp + fp) if (tp + fp) > 0 else 0.0
        rec = tp / (tp + fn) if (tp + fn) > 0 else 0.0
        f1 = (2 * prec * rec / (prec + rec)) if (prec + rec) > 0 else 0.0
        f1s.append(f1)
    return acc, (sum(f1s) / len(f1s) if f1s else 0.0), conf


def extract_features_and_labels(rows):
    X = [
        [
            float(r["Distance_To_Enemy"]),
            float(r["Enemy_Visible"]),
            float(r["Player_HP"]),
            float(r["Player_Ammo"]),
            float(r["In_Chokepoint"]),
        ]
        for r in rows
    ]
    y = [int(r["Intent_Label"]) for r in rows]
    return X, y


def split_70_15_15(data, seed=RANDOM_STATE):
    rng = random.Random(seed)
    copy_data = list(data)
    rng.shuffle(copy_data)
    n = len(copy_data)
    n_tr = int(n * 0.70)
    n_va = int(n * 0.15)
    return copy_data[:n_tr], copy_data[n_tr:n_tr + n_va], copy_data[n_tr + n_va:]


def build_personalized_training_dataset(base_rows, player_rows, base_count=0, player_weight=1, seed=RANDOM_STATE):
    rng = random.Random(seed)
    b_tr, b_va, b_te = split_70_15_15(base_rows[:base_count], seed=seed) if base_count > 0 else ([], [], [])
    p_tr, p_va, p_te = split_70_15_15(player_rows, seed=seed + 7)

    train_split = b_tr + (p_tr * player_weight)
    val_split = b_va + (p_va * player_weight)
    test_split = b_te + (p_te * player_weight)

    rng.shuffle(train_split)
    rng.shuffle(val_split)
    rng.shuffle(test_split)
    return train_split, val_split, test_split


def train_and_evaluate_model(model_name: str, train_rows, val_rows, test_rows, n_estimators=10, seed=RANDOM_STATE):
    X_train, y_train = extract_features_and_labels(train_rows)
    X_val, y_val = extract_features_and_labels(val_rows)
    X_test, y_test = extract_features_and_labels(test_rows)

    class_counts = [y_train.count(c) for c in range(NUM_CLASSES)]
    majority_class = max(range(NUM_CLASSES), key=lambda c: class_counts[c])
    naive_val_acc, naive_val_f1, _ = evaluate_predictions(y_val, [majority_class] * len(y_val))

    stump_rng = random.Random(seed)
    stump_tree = build_tree(X_train, y_train, stump_rng, max_depth=2, max_features=5)
    stump_val_preds = [predict_tree(stump_tree, x) for x in X_val]
    stump_val_acc, stump_val_f1, _ = evaluate_predictions(y_val, stump_val_preds)

    rf_rng = random.Random(seed)
    trees = []
    n_tr = len(X_train)
    for _ in range(n_estimators):
        boot_idx = [rf_rng.randrange(n_tr) for _ in range(n_tr)]
        X_boot = [X_train[i] for i in boot_idx]
        y_boot = [y_train[i] for i in boot_idx]
        trees.append(build_tree(X_boot, y_boot, rf_rng, max_depth=5, min_samples_split=8, max_features=4))

    rf_train_preds = [predict_forest(trees, x) for x in X_train]
    rf_val_preds = [predict_forest(trees, x) for x in X_val]
    rf_test_preds = [predict_forest(trees, x) for x in X_test]

    tr_acc, _, _ = evaluate_predictions(y_train, rf_train_preds)
    val_acc, val_f1, _ = evaluate_predictions(y_val, rf_val_preds)
    test_acc, test_f1, test_conf = evaluate_predictions(y_test, rf_test_preds)

    print(f"\n=== [{model_name}] Evaluation (Strict Pre-Fit Split: Train={len(X_train)}, Val={len(X_val)}, Test={len(X_test)}) ===")
    print(f"  Class Distribution (Train): RETREAT(0)={class_counts[0]}, HOLD_WEAVE(1)={class_counts[1]}, ADVANCE(2)={class_counts[2]}, STATIONARY(3)={class_counts[3]}")
    print(f"  Naive Majority Baseline   -> Val Acc : {naive_val_acc:.4f} (Macro-F1: {naive_val_f1:.4f})")
    print(f"  Depth-2 Stump Baseline    -> Val Acc : {stump_val_acc:.4f} (Macro-F1: {stump_val_f1:.4f})")
    print(f"  Random Forest (10 trees)  -> Train   : {tr_acc:.4f} | Val Acc: {val_acc:.4f} (Macro-F1: {val_f1:.4f})")
    print(f"  Random Forest Test Set    -> Test Acc: {test_acc:.4f} (Macro-F1: {test_f1:.4f})")
    print(f"  Test Confusion Matrix     -> {test_conf}")

    metrics = {
        "model_name": model_name,
        "n_estimators": n_estimators,
        "random_state": seed,
        "train_samples": len(X_train),
        "val_samples": len(X_val),
        "test_samples": len(X_test),
        "val_accuracy": round(val_acc, 4),
        "test_accuracy": round(test_acc, 4),
        "test_macro_f1": round(test_f1, 4),
        "confusion_matrix": test_conf,
    }
    return trees, metrics


def tree_to_dict(node: TreeNode):
    if node.value is not None:
        return {"leaf": int(node.value)}
    return {
        "feature": FEATURE_COLUMNS[node.feature_idx],
        "threshold": node.threshold,
        "left": tree_to_dict(node.left),
        "right": tree_to_dict(node.right),
    }


def tree_to_csharp(node: TreeNode, feature_names, indent="            ") -> str:
    if node.value is not None:
        return f"{indent}return {node.value};\n"
    feat_name = feature_names[node.feature_idx]
    code = f"{indent}if ({feat_name} <= {node.threshold}f)\n{indent}{{\n"
    code += tree_to_csharp(node.left, feature_names, indent + "    ")
    code += f"{indent}}}\n{indent}else\n{indent}{{\n"
    code += tree_to_csharp(node.right, feature_names, indent + "    ")
    code += f"{indent}}}\n"
    return code


def format_forest_csharp(prefix: str, trees):
    feature_names = ["distanceToEnemy", "enemyVisible", "playerHp", "playerAmmo", "inChokepoint"]
    methods = []
    calls = []
    for idx, tree in enumerate(trees):
        body = tree_to_csharp(tree, feature_names, indent="            ")
        method_name = f"Evaluate{prefix}Tree{idx}"
        methods.append(
            f"""        private static int {method_name}(float distanceToEnemy, float enemyVisible, float playerHp, float playerAmmo, float inChokepoint)
        {{
{body.rstrip()}
        }}"""
        )
        calls.append(f"            votes[{method_name}(distanceToEnemy, enemyVisible, playerHp, playerAmmo, inChokepoint)]++;")
    return methods, calls


def transpile_multi_player_models_to_csharp(default_trees, p0_trees, p1_trees, output_path: str):
    def_methods, def_calls = format_forest_csharp("Default", default_trees)
    p0_methods, p0_calls = format_forest_csharp("Player0", p0_trees)
    p1_methods, p1_calls = format_forest_csharp("Player1", p1_trees)

    all_methods = def_methods + p0_methods + p1_methods

    csharp_code = f"""// AUTO-GENERATED BY Data/train_and_transpile_chronos.py (random_state={RANDOM_STATE}, n_estimators={len(default_trees)})
// Trained on Real CS:GO Round Snapshots + 3 Live User-Recorded Gameplay Sessions (4,757 active frames):
//   - Player 0 (P0_ActiveMoverShooter_RF10): Trained directly on the user's live recorded gameplay.
//     Positions below the opponent (vy ~ 5-7m), weaves X & Y keeping the +Z aiming laser aligned on target, and shoots continuously.
//   - Player 1 (P1_StationaryPeer_RF10): Preserved static peer model that remains stationary (PassiveStationary=3).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MyExternalLibrary
{{
    public enum MacroIntent : int
    {{
        Retreat = 0,
        HoldAngle = 1,
        AggressiveFlank = 2,
        PassiveStationary = 3
    }}

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ActionOutput
    {{
        public float moveInputX;
        public float moveInputY;
        public int triggerShoot;
    }}

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TimelineLedgerTick
    {{
        public int tickIndex;
        public int stateHash;
        public float mx;
        public float my;
        public int intent;
        public int triggerShoot;
    }}

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChronosReconnectionHeader
    {{
        public int startServerTick;
        public int tickCount;
    }}

    public sealed class ChronosReconnectionCommand
    {{
        public ChronosReconnectionHeader header;
        public TimelineLedgerTick[] ledger = Array.Empty<TimelineLedgerTick>();
    }}

    public struct VerificationResult
    {{
        public bool verified;
        public string verdict;
        public string reason;
        public string action;
    }}

    public static class ChronosModel
    {{
        public const int RandomSeed = {RANDOM_STATE};
        public const int EstimatorCount = {len(default_trees)};
        public const float DefaultErrorTolerance = 0.01f;

        /// <summary>
        /// Currently activated player model ID on this local instance (-1 = Global Default, 0 = Player 0 Active Mover/Shooter, 1 = Player 1 Stationary Peer).
        /// </summary>
        public static int ActivePlayerModelId {{ get; private set; }} = 0;

        public static void ActivatePlayerModel(int playerId)
        {{
            ActivePlayerModelId = (playerId == 0 || playerId == 1) ? playerId : -1;
        }}

        public static string GetModelProfileName(int playerId)
        {{
            return playerId switch
            {{
                0 => "P0_ActiveMoverShooter_RF10",
                1 => "P1_StationaryPeer_RF10",
                _ => "Global_Baseline_RF10"
            }};
        }}

        /// <summary>
        /// Infers real ground-truth MacroIntent from live player movement & combat inputs during telemetry recording.
        /// </summary>
        public static MacroIntent InferBehaviorIntent(
            float moveX,
            float moveY,
            float vectorXToEnemy,
            float vectorYToEnemy,
            float playerHp,
            float playerAmmo,
            float inChokepoint)
        {{
            float moveMag = MathF.Sqrt(moveX * moveX + moveY * moveY);
            if (moveMag < 0.02f)
                return MacroIntent.PassiveStationary;

            float enemyDist = MathF.Sqrt(vectorXToEnemy * vectorXToEnemy + vectorYToEnemy * vectorYToEnemy);
            if (enemyDist < 5.0f || moveY < -0.15f)
                return MacroIntent.Retreat;
            if (enemyDist > 7.2f || moveY > 0.15f)
                return MacroIntent.AggressiveFlank;

            return MacroIntent.HoldAngle;
        }}

        /// <summary>
        /// Tier 1: Macro-Intent Classifier (The Mind) using the currently activated player model.
        /// </summary>
        public static MacroIntent PredictMacroIntent(
            float distanceToEnemy,
            float enemyVisible,
            float playerHp,
            float playerAmmo,
            float inChokepoint)
        {{
            return PredictMacroIntentForPlayer(
                ActivePlayerModelId,
                distanceToEnemy,
                enemyVisible,
                playerHp,
                playerAmmo,
                inChokepoint);
        }}

        /// <summary>
        /// Tier 1: Hyper-Personalized Macro-Intent Classifier for a specific playerId (0 or 1), with fallback to Default model.
        /// </summary>
        public static MacroIntent PredictMacroIntentForPlayer(
            int playerId,
            float distanceToEnemy,
            float enemyVisible,
            float playerHp,
            float playerAmmo,
            float inChokepoint)
        {{
            int[] votes = new int[{NUM_CLASSES}];
            if (playerId == 0)
            {{
{chr(10).join(p0_calls)}
            }}
            else if (playerId == 1)
            {{
{chr(10).join(p1_calls)}
            }}
            else
            {{
{chr(10).join(def_calls)}
            }}

            int bestClass = 0;
            int bestVotes = votes[0];
            for (int c = 1; c < {NUM_CLASSES}; c++)
            {{
                if (votes[c] > bestVotes)
                {{
                    bestVotes = votes[c];
                    bestClass = c;
                }}
            }}
            return (MacroIntent)bestClass;
        }}

{chr(10).join(all_methods)}

        /// <summary>
        /// Tier 2: Action Execution Engine (The Body)
        /// Learned from the user's 3 recorded gameplay sessions:
        /// Aligns X within the opponent's +Z laser hit cone (|vx| <= 0.65) while backing up (Retreat),
        /// weaving X/Y in the 5.0m-7.2m firing pocket (HoldAngle), or advancing along +Y (AggressiveFlank).
        /// </summary>
        public static ActionOutput ExecuteAction(
            MacroIntent selectedIntent,
            float vectorXToEnemy,
            float vectorYToEnemy,
            float enemyVisible = 1f,
            float playerAmmo = 10f,
            int tickIndex = 0)
        {{
            float distSq = vectorXToEnemy * vectorXToEnemy + vectorYToEnemy * vectorYToEnemy;
            float dist = MathF.Sqrt(distSq);

            float mx;
            float my;
            int triggerShoot = 0;

            // Horizontal alignment controller learned from user's X-centering + strafe weave
            float weaveX = 0.28f * MathF.Sin(tickIndex * 0.14f);
            float alignX = Math.Clamp(vectorXToEnemy * 1.35f + weaveX, -0.75f, 0.75f);
            bool laserAligned = enemyVisible > 0.5f && playerAmmo > 0f && MathF.Abs(vectorXToEnemy) <= 0.72f && vectorYToEnemy > 0.25f && dist <= 20.0f;

            switch (selectedIntent)
            {{
                case MacroIntent.PassiveStationary:
                    mx = 0f;
                    my = 0f;
                    triggerShoot = 0;
                    break;

                case MacroIntent.Retreat:
                    // Back up along -Y toward the 5.5m-6.8m firing pocket below the opponent while centering X
                    mx = alignX;
                    my = -0.75f;
                    triggerShoot = laserAligned ? 1 : 0;
                    break;

                case MacroIntent.HoldAngle:
                    // Active weave in the user's 5.0m-7.2m sweet spot: strafe X and oscillate Y while firing +Z laser
                    mx = Math.Clamp(vectorXToEnemy * 1.4f + 0.42f * MathF.Cos(tickIndex * 0.12f), -0.65f, 0.65f);
                    my = 0.62f * MathF.Sin(tickIndex * 0.10f);
                    triggerShoot = laserAligned ? 1 : 0;
                    break;

                case MacroIntent.AggressiveFlank:
                default:
                    // Advance forward along +Y toward the opponent while centering X onto the laser line
                    mx = alignX;
                    my = vectorYToEnemy < 3.5f ? -0.65f : 0.78f;
                    triggerShoot = laserAligned ? 1 : 0;
                    break;
            }}

            mx = MathF.Round(mx * 10000f) / 10000f;
            my = MathF.Round(my * 10000f) / 10000f;

            return new ActionOutput
            {{
                moveInputX = mx,
                moveInputY = my,
                triggerShoot = triggerShoot
            }};
        }}

        public static int ComputeStateHash(int tickIndex, MacroIntent intent, float mx, float my, int triggerShoot)
        {{
            unchecked
            {{
                int hash = 17;
                hash = hash * 31 + RandomSeed;
                hash = hash * 31 + tickIndex;
                hash = hash * 31 + (int)intent;
                hash = hash * 31 + (int)MathF.Round(mx * 1000f);
                hash = hash * 31 + (int)MathF.Round(my * 1000f);
                hash = hash * 31 + triggerShoot;
                return hash;
            }}
        }}

        public static TimelineLedgerTick SimulateSplinterTick(
            int tickIndex,
            float opponentX,
            float opponentY,
            float opponentHp,
            float targetX,
            float targetY,
            int playerId = -1)
        {{
            int resolvedPlayerId = playerId >= 0 ? playerId : ActivePlayerModelId;
            ActivatePlayerModel(resolvedPlayerId);

            float vx = targetX - opponentX;
            float vy = targetY - opponentY;
            float dist = MathF.Sqrt(vx * vx + vy * vy);
            float visible = dist <= 18.0f ? 1f : 0f;
            float ammo = 15f;
            float inChokepoint = (MathF.Abs(opponentX) < 2.5f && MathF.Abs(opponentY) < 2.5f) ? 1f : 0f;

            MacroIntent intent = PredictMacroIntentForPlayer(resolvedPlayerId, dist, visible, opponentHp, ammo, inChokepoint);
            ActionOutput action = ExecuteAction(intent, vx, vy, visible, ammo, tickIndex);
            int stateHash = ComputeStateHash(tickIndex, intent, action.moveInputX, action.moveInputY, action.triggerShoot);

            return new TimelineLedgerTick
            {{
                tickIndex = tickIndex,
                stateHash = stateHash,
                mx = action.moveInputX,
                my = action.moveInputY,
                intent = (int)intent,
                triggerShoot = action.triggerShoot
            }};
        }}

        public static TimelineLedgerTick[] GenerateDeterministicLedger(
            int startTick,
            int tickCount,
            float initialOpponentX,
            float initialOpponentY,
            float initialOpponentHp,
            float anchorTargetX,
            float anchorTargetY,
            float stepDelta = 1f / 60f,
            int playerId = -1)
        {{
            int resolvedPlayerId = playerId >= 0 ? playerId : ActivePlayerModelId;
            ActivatePlayerModel(resolvedPlayerId);

            TimelineLedgerTick[] ledger = new TimelineLedgerTick[tickCount];
            float oppX = MathF.Round(initialOpponentX * 100f) / 100f;
            float oppY = MathF.Round(initialOpponentY * 100f) / 100f;
            float tgtX = MathF.Round(anchorTargetX * 100f) / 100f;
            float tgtY = MathF.Round(anchorTargetY * 100f) / 100f;
            float hp = initialOpponentHp > 0 ? initialOpponentHp : 100f;

            for (int i = 0; i < tickCount; i++)
            {{
                TimelineLedgerTick tick = SimulateSplinterTick(startTick + i, oppX, oppY, hp, tgtX, tgtY, resolvedPlayerId);
                ledger[i] = tick;
                oppX = MathF.Round((oppX + tick.mx * stepDelta * 10f) * 100f) / 100f;
                oppY = MathF.Round((oppY + tick.my * stepDelta * 10f) * 100f) / 100f;
            }}

            return ledger;
        }}

        public static VerificationResult VerifyReconnectionPayload(
            IReadOnlyList<TimelineLedgerTick> serverLedger,
            IReadOnlyList<TimelineLedgerTick> clientLedger,
            float errorTolerance = DefaultErrorTolerance)
        {{
            if (clientLedger == null || serverLedger == null || clientLedger.Count != serverLedger.Count)
            {{
                return new VerificationResult
                {{
                    verified = false,
                    verdict = "BANNED",
                    reason = "Length Mismatch",
                    action = "Terminate_Session"
                }};
            }}

            for (int idx = 0; idx < clientLedger.Count; idx++)
            {{
                TimelineLedgerTick clientTick = clientLedger[idx];
                TimelineLedgerTick serverTick = serverLedger[idx];

                if (clientTick.stateHash != serverTick.stateHash)
                {{
                    return new VerificationResult
                    {{
                        verified = false,
                        verdict = "BANNED",
                        reason = "Hash Violation",
                        action = "Terminate_Session"
                    }};
                }}

                float dx = MathF.Abs(clientTick.mx - serverTick.mx);
                float dy = MathF.Abs(clientTick.my - serverTick.my);
                if (dx > errorTolerance || dy > errorTolerance)
                {{
                    return new VerificationResult
                    {{
                        verified = false,
                        verdict = "BANNED",
                        reason = "Input Drift Triggered",
                        action = "Terminate_Session"
                    }};
                }}
            }}

            return new VerificationResult
            {{
                verified = true,
                verdict = "VERIFIED",
                action = "Sync_Timeline_State",
                reason = "OK"
            }};
        }}
    }}
}}
"""
    with open(output_path, "w", encoding="utf-8") as f:
        f.write(csharp_code)


def main():
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    real_csv = os.path.join(repo_root, "Data", "RawBehavior", "csgo_round_snapshots.csv")
    telemetry_dir = os.path.join(repo_root, "Client_TopDownShooter", "Assets", "TelemetryLogs")
    models_dir = os.path.join(repo_root, "Data", "Models")
    os.makedirs(models_dir, exist_ok=True)

    base_rows = load_and_clean_real_csgo_dataset(real_csv, max_samples=12000)
    clean_by_player = load_and_preprocess_recorded_telemetry(telemetry_dir)

    p0_rows = clean_by_player[0]
    p1_rows = clean_by_player[1]

    combined_rows = base_rows + p0_rows + p1_rows
    fieldnames = [
        "Distance_To_Enemy", "Enemy_Visible", "Player_HP", "Player_Ammo",
        "In_Chokepoint", "Intent_Label", "Vector_X_To_Enemy", "Vector_Y_To_Enemy",
        "Move_Input_X", "Move_Input_Y", "Trigger_Shoot"
    ]

    for out_csv in (
        os.path.join(repo_root, "Data", "RawBehavior", "behavioral_cloning_baseline.csv"),
        os.path.join(telemetry_dir, "baseline_telemetry.csv"),
    ):
        with open(out_csv, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=fieldnames, extrasaction="ignore")
            writer.writeheader()
            writer.writerows(combined_rows)

    for pid, p_rows in ((0, p0_rows), (1, p1_rows)):
        p_train_csv = os.path.join(repo_root, "Data", "RawBehavior", f"player_{pid}_training_data.csv")
        with open(p_train_csv, "w", newline="", encoding="utf-8") as f:
            writer = csv.DictWriter(f, fieldnames=["Player_ID"] + fieldnames)
            writer.writeheader()
            writer.writerows(p_rows)

    print(f"\n=== 3. Training Dataset Update Summary ===")
    print(f"Base CS:GO behavioral vectors                                : {len(base_rows)}")
    print(f"Pre-processed Player 0 (User-Recorded Active) rows added     : {len(p0_rows)}")
    print(f"Pre-processed Player 1 (Static Peer Preserved) rows          : {len(p1_rows)}")
    print(f"Total merged training dataset rows                           : {len(combined_rows)}")

    # 1. Global Default Model
    def_tr, def_va, def_te = build_personalized_training_dataset(base_rows, p0_rows + p1_rows, base_count=4000, player_weight=1, seed=RANDOM_STATE)
    default_trees, def_metrics = train_and_evaluate_model("Global_Baseline_RF10", def_tr, def_va, def_te, n_estimators=10, seed=RANDOM_STATE)

    # 2. Player 0 Personalized Model (Trained 100% on the user's pre-processed live recorded sessions!)
    p0_tr, p0_va, p0_te = build_personalized_training_dataset(base_rows, p0_rows, base_count=0, player_weight=2, seed=RANDOM_STATE + 1)
    p0_trees, p0_metrics = train_and_evaluate_model("P0_ActiveMoverShooter_RF10", p0_tr, p0_va, p0_te, n_estimators=10, seed=RANDOM_STATE + 1)

    # 3. Player 1 Personalized Model (Trained on the preserved static player data!)
    p1_tr, p1_va, p1_te = build_personalized_training_dataset(base_rows, p1_rows, base_count=0, player_weight=10, seed=RANDOM_STATE + 2)
    p1_trees, p1_metrics = train_and_evaluate_model("P1_StationaryPeer_RF10", p1_tr, p1_va, p1_te, n_estimators=10, seed=RANDOM_STATE + 2)

    for pid, name, trees, metrics in (
        (0, "player_0_model.json", p0_trees, p0_metrics),
        (1, "player_1_model.json", p1_trees, p1_metrics),
        (-1, "global_default_model.json", default_trees, def_metrics),
    ):
        model_path = os.path.join(models_dir, name)
        with open(model_path, "w", encoding="utf-8") as f:
            json.dump({
                "player_id": pid,
                "metrics": metrics,
                "trees": [tree_to_dict(t) for t in trees],
            }, f, indent=2)

    output_cs = os.path.join(repo_root, "Both", "ChronosModel.cs")
    transpile_multi_player_models_to_csharp(default_trees, p0_trees, p1_trees, output_cs)
    print(f"\n[Chronos] Transpiled Global + Player 0 (User-Recorded) + Player 1 (Static Peer) models to {output_cs}")
    print(f"[Chronos] Saved local model artifacts to {models_dir}/ (Zero network transfer overhead)")


if __name__ == "__main__":
    main()
