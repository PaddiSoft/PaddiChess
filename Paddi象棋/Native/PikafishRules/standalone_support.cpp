// GPL-3.0-or-later. Glue for upstream position.cpp without the search runtime.
#include <cstdlib>
#include "tt.h"
#include "uci.h"

namespace Stockfish {
// Used only by upstream's diagnostic board printer.
std::string UCIEngine::square(Square s) {
    return std::string{char('a' + file_of(s)), char('0' + rank_of(s))};
}
// Every rule-adapter do_move call passes the default null TT. Fail explicitly
// if future adapter code tries to use a search transposition table here.
TTEntry* TranspositionTable::first_entry(const Key) const { std::abort(); }
}
