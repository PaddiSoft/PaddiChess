// Paddi's protocol adapter for the unmodified Pikafish position/rules implementation.
// GPL-3.0-or-later; see upstream/Copying.txt and SOURCE.md.
#include <deque>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#include "attacks.h"
#include "movegen.h"
#include "position.h"

using namespace Stockfish;

static std::string quote(const std::string& text) {
    std::string result = "\"";
    for (unsigned char c : text) {
        if (c == '\\' || c == '"') { result += '\\'; result += char(c); }
        else if (c >= 32) result += char(c);
        else result += ' ';
    }
    return result + '"';
}

static std::string coordinate(Move move) {
    std::string result;
    for (Square s : {move.from_sq(), move.to_sq()}) {
        result += char('a' + file_of(s));
        result += char('0' + rank_of(s));
    }
    return result;
}

static std::string array(const std::vector<std::string>& moves) {
    std::string result = "[";
    for (const auto& move : moves) {
        if (result.size() > 1) result += ',';
        result += quote(move);
    }
    return result + ']';
}

static void rules(const std::string& fen, const std::string& history) {
    std::deque<StateInfo> states(1);
    Position position;
    if (auto error = position.set(fen, &states.back())) throw *error;
    std::istringstream input(history);
    std::string text;
    int plies = 0;
    while (input >> text) {
        Move found = Move::none();
        for (Move move : MoveList<LEGAL>(position))
            if (coordinate(move) == text) { found = move; break; }
        if (found == Move::none()) throw std::runtime_error("Invalid history move: " + text);
        if (++plies > 10000) throw std::runtime_error("History exceeds 10000 plies");
        states.emplace_back();
        position.do_move(found, states.back());
    }
    const auto currentFen = position.fen();
    std::vector<std::string> allowed, excluded;
    for (Move move : MoveList<LEGAL>(position)) {
        StateInfo after;
        position.do_move(move, after);
        Value outcome = VALUE_NONE;
        // At the new real position (ply 0), only a definitive native rule result
        // against the mover excludes a move. Draw repetitions remain allowed.
        // This performs no evaluation/search and cannot reject a tactically poor move.
        const bool losesByRule = position.rule_judge(outcome, 0) && is_win(outcome);
        position.undo_move(move);
        (losesByRule ? excluded : allowed).push_back(coordinate(move));
    }
    std::cout << "{\"protocol\":1,\"ruleVersion\":\"Pikafish-2026-09-06\",\"fen\":"
              << quote(currentFen) << ",\"historyPlies\":" << plies
              << ",\"allowed\":" << array(allowed) << ",\"excluded\":" << array(excluded) << "}\n";
}

int main() {
    Attacks::init();
    Position::init();
    std::string line;
    while (std::getline(std::cin, line)) {
        if (line == "quit") return 0;
        try {
            const auto first = line.find('\t');
            const auto second = first == std::string::npos ? first : line.find('\t', first + 1);
            if (first == std::string::npos || second == std::string::npos || line.substr(0, first) != "rules")
                throw std::runtime_error("Expected rules, FEN and history separated by tabs");
            rules(line.substr(first + 1, second - first - 1), line.substr(second + 1));
        } catch (const std::exception& error) {
            std::cout << "{\"protocol\":1,\"error\":" << quote(error.what()) << "}\n";
        }
        std::cout.flush();
    }
}
